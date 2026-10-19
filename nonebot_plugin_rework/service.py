"""``/rework2`` 指令解析与业务编排。"""

from __future__ import annotations

import asyncio
import base64
import itertools
import logging
import re
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlparse
from urllib.request import url2pathname

import aiohttp
from nonebot.adapters.onebot.v11 import (
    Bot,
    GroupMessageEvent,
    Message,
    MessageEvent,
    MessageSegment,
)

from . import archive, osu_source
from .beatmap import BeatmapInfo, parse_beatmap
from .cache import ChartCache
from .config import AppConfig
from .errorcard import render_error_card
from .osu_source import BeatmapFile, OsuSourceError
from .renderer import RenderError, Renderer, _prepare_sibling_toolkit

logger = logging.getLogger(__name__)

HELP_TEXT = (
    "【Rework 谱面分析】\n"
    "/rework2 <谱面ID>       分析单张谱面\n"
    "/rework2 bid<谱面ID>    同上（显式指定谱面，亦可 b<ID>）\n"
    "/rework2 sid<谱包ID>    分析整个谱包（合并转发发送，亦可 s<ID>）\n"
    "/rework2 c<CID>         分析 Malody 谱面（亦可 cid<CID>）\n"
    "/rework2 <谱面> +dt/+ht  支持 DT、HT 和 *2.5 自定义变速\n"
    "/rework2 <osu! 链接>   支持官网谱面 / 谱包链接\n"
    "回复一条谱面文件并发送 /rework2\n"
    "  支持 .osu / .osz / .mc / .mcz，以及内含它们的 .zip（可嵌套）\n"
    "（所有 osu! 模式均可生成卡片；RC/LN 固定密度仅对 4K mania 显示）"
)

_ID_RE = re.compile(r"^\d+$")
# b / bid / s / sid 与数字之间不需要空格：bid123、bid 123、s456 均有效
_KIND_ID_RE = re.compile(r"^(bid|sid|b|s)\s*(\d+)$", re.IGNORECASE)
_CID_RE = re.compile(r"^(cid|c)\s*(\d+)$", re.IGNORECASE)
_KIND_ALIASES = {
    "b": "beatmap", "bid": "beatmap", "beatmap": "beatmap",
    "s": "set", "sid": "set", "set": "set", "setid": "set",
}
_request_seq = itertools.count(1)


@dataclass
class Command:
    kind: str  # beatmap / set / cid / auto / reply / help
    value: int | None = None
    payload: BeatmapFile | None = None  # 探测阶段已下载的谱面，避免重复请求
    speed_rate: float = 1.0
    mods: tuple[str, ...] = ()
    od_flag: str | float | None = None
    cvt_flag: str | None = None


_KNOWN_MODS = ("NC", "DT", "HT", "HR", "EZ", "DC", "IN", "HO")


def _parse_command_options(tokens: list[str]) -> tuple[float, tuple[str, ...], str | float | None, str | None, str | None]:
    """解析参考插件同款的 mods / 自定义速率，并返回未消费的来源 token。"""
    speed_rate = 1.0
    mods: list[str] = []
    od_flag: str | float | None = None
    cvt_flag: str | None = None
    source_token: str | None = None
    errors: list[str] = []

    def add_mods(value: str) -> None:
        nonlocal speed_rate, od_flag, cvt_flag
        compact = value[1:].upper().replace("+", "")
        index = 0
        while index < len(compact):
            matched = next((code for code in _KNOWN_MODS if compact.startswith(code, index)), None)
            if matched is None:
                errors.append(f"不支持的 mods: {compact[index:]}")
                break
            mods.append(matched)
            index += len(matched)
            if matched in ("DT", "NC"):
                speed_rate *= 1.5
            elif matched in ("HT", "DC"):
                speed_rate *= 0.75
            elif matched in ("HR", "EZ"):
                if od_flag is not None and od_flag != matched:
                    errors.append(f"EZ/HR 模组冲突: 已设置 {od_flag}, 当前 {matched}")
                else:
                    od_flag = matched
            elif matched in ("IN", "HO"):
                if cvt_flag is not None and cvt_flag != matched:
                    errors.append(f"模组冲突: 已设置 {cvt_flag}, 当前 {matched}")
                else:
                    cvt_flag = matched

    index = 0
    while index < len(tokens):
        token = tokens[index]
        lower = token.lower()
        if token.startswith("+"):
            add_mods(token)
        elif lower.startswith(("x", "×")) or token.startswith("*"):
            raw_rate = token[1:]
            try:
                value = round(float(raw_rate), 3)
            except ValueError:
                errors.append(f"无效的倍速: {raw_rate}")
            else:
                if 0.25 <= value <= 3.0:
                    speed_rate = value
                else:
                    errors.append(f"倍速必须在0.25到3.0之间: {raw_rate}x")
        elif lower.startswith("od"):
            raw_od = token[2:]
            try:
                value = float(raw_od)
            except ValueError:
                errors.append(f"无效的OD: {raw_od}")
            else:
                if -15 <= value <= 15:
                    od_flag = value
                else:
                    errors.append(f"OD值必须在-15到15之间: OD{value}")
        elif source_token is None:
            source_token = token
        index += 1

    if errors:
        return speed_rate, tuple(mods), od_flag, cvt_flag, "；".join(errors)
    return speed_rate, tuple(mods), od_flag, cvt_flag, source_token


@dataclass
class ResultItem:
    name: str
    info: BeatmapInfo | None
    png: bytes | None = None
    error: str | None = None
    key: str | None = None  # 谱面哈希（错误卡片为 None）


def parse_arguments(text: str) -> Command:
    """解析 ``/rework2`` 后面的参数（NoneBot 已剥离指令本身）。"""
    rest = text.strip()
    if not rest:
        return Command(kind="reply")
    tokens = []
    for token in rest.split():
        attached = re.match(r"^(?P<source>(?:bid|sid|beatmap|set|b|s|cid|c)?\d+)\+(?P<mods>.+)$", token, re.IGNORECASE)
        if attached:
            tokens.extend([attached.group("source"), "+" + attached.group("mods")])
        else:
            tokens.append(token)
    speed_rate, mods, od_flag, cvt_flag, source_or_error = _parse_command_options(tokens)
    if source_or_error is None:
        return Command(kind="reply", speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)
    if source_or_error and ("；" in source_or_error or source_or_error.startswith(("不支持的", "无效的", "倍速必须", "EZ/HR", "模组冲突", "OD值"))):
        return Command(kind="help")

    source = source_or_error
    url_kind = osu_source.parse_osu_url(source)
    if url_kind is not None:
        return Command(kind=url_kind[0], value=url_kind[1], speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)

    cid_match = _CID_RE.match(source)
    if cid_match:
        return Command(kind="cid", value=int(cid_match.group(2)), speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)

    kind_match = _KIND_ID_RE.match(source)
    if kind_match:
        return Command(kind=_KIND_ALIASES[kind_match.group(1).lower()], value=int(kind_match.group(2)), speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)

    if source.lower() in _KIND_ALIASES and len(tokens) >= 2 and tokens[1].isdigit():
        return Command(kind=_KIND_ALIASES[source.lower()], value=int(tokens[1]), speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)

    if source.lower() in ("c", "cid") and len(tokens) >= 2 and tokens[1].isdigit():
        return Command(kind="cid", value=int(tokens[1]), speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)

    if _ID_RE.match(source):
        return Command(kind="auto", value=int(source), speed_rate=speed_rate, mods=mods, od_flag=od_flag, cvt_flag=cvt_flag)

    return Command(kind="help")


def is_allowed(cfg: AppConfig, event: MessageEvent) -> bool:
    """按插件配置判断该事件是否允许触发。"""
    if isinstance(event, GroupMessageEvent):
        if not cfg.access.allow_group:
            return False
        if cfg.access.group_allowlist and str(event.group_id) not in cfg.access.group_allowlist:
            return False
    elif not cfg.access.allow_private:
        return False
    return str(event.user_id) not in cfg.access.user_denylist


def _text_node(text: str) -> dict[str, Any]:
    return {"type": "text", "data": {"text": text}}


def _image_node(png: bytes) -> dict[str, Any]:
    encoded = base64.b64encode(png).decode("ascii")
    return {"type": "image", "data": {"file": f"base64://{encoded}"}}


def _forward_node(name: str, uin: str, content: list[dict[str, Any]]) -> dict[str, Any]:
    return {"type": "node", "data": {"name": name, "uin": str(uin), "content": content}}


class ReworkService:
    """单例业务对象：下载 / 解包 / 渲染 / 发送。"""

    def __init__(self, cfg: AppConfig, renderer: Renderer) -> None:
        self.cfg = cfg
        self.renderer = renderer
        self._session: aiohttp.ClientSession | None = None
        self.cache = ChartCache(cfg.storage.data_dir, enabled=cfg.storage.enable_cache)

    async def start(self) -> None:
        if self._session is None:
            self._session = aiohttp.ClientSession()
        charts, images = self.cache.stats()
        logger.info(
            "谱面库：%d 条记录 / %d 张图片（缓存%s）",
            charts, images, "开启" if self.cfg.storage.enable_cache else "关闭",
        )

    async def close(self) -> None:
        if self._session is not None:
            await self._session.close()
            self._session = None

    # ------------------------------------------------------------------ 指令入口
    async def handle(self, bot: Bot, event: MessageEvent, raw_args: str) -> None:
        command = parse_arguments(raw_args)
        tag = f"[#{next(_request_seq)}]"
        scope = f"群 {event.group_id}" if isinstance(event, GroupMessageEvent) else f"用户 {event.user_id}"
        logger.info("%s 收到指令：%r（来自%s）", tag, raw_args.strip(), scope)
        started = time.perf_counter()

        if command.kind == "help":
            logger.info("%s 返回用法说明", tag)
            await bot.send(event, MessageSegment.text(HELP_TEXT))
            return

        try:
            command = await self._resolve_auto(command, tag)
            files, ack = await self._collect_sources(bot, command, event, tag)
            if ack:
                await bot.send(event, MessageSegment.text(ack))
            if not files:
                logger.warning("%s 没有获取到任何谱面文件", tag)
                await bot.send(event, MessageSegment.text("没有获取到任何谱面文件。\n\n" + HELP_TEXT))
                return

            await self._render_and_send(bot, event, files, tag, command)
            logger.info("%s 处理完成，总用时 %.1fs", tag, time.perf_counter() - started)
        except OsuSourceError as exc:
            logger.warning("%s 获取谱面失败：%s", tag, exc)
            await bot.send(event, MessageSegment.text(f"获取谱面失败：{exc}"))
        except RenderError as exc:
            logger.error("%s 渲染失败：%s", tag, exc)
            await bot.send(event, MessageSegment.text(f"渲染失败：{exc}"))
        except Exception as exc:  # noqa: BLE001
            logger.exception("%s 处理 /rework2 请求时发生异常", tag)
            await bot.send(event, MessageSegment.text(f"处理失败：{exc}"))

    # ------------------------------------------------------------------ 谱面来源
    async def _resolve_auto(self, command: Command, tag: str) -> Command:
        """裸 ID：先按谱面 ID 试，失败再按谱包 ID 试。"""
        if command.kind != "auto" or command.value is None:
            return command
        assert self._session is not None
        logger.info("%s ID=%s 未指定类型，先按谱面 ID 探测…", tag, command.value)
        try:
            fetched = await osu_source.fetch_beatmap(self._session, self.cfg.osu, command.value)
            logger.info("%s ID=%s 识别为单张谱面", tag, command.value)
            return Command(kind="beatmap", value=command.value, payload=fetched, speed_rate=command.speed_rate, mods=command.mods, od_flag=command.od_flag, cvt_flag=command.cvt_flag)
        except OsuSourceError as exc:
            logger.info("%s ID=%s 不是可用谱面（%s），改按谱包处理", tag, command.value, exc)
            return Command(kind="set", value=command.value, speed_rate=command.speed_rate, mods=command.mods, od_flag=command.od_flag, cvt_flag=command.cvt_flag)

    async def _collect_sources(
        self,
        bot: Bot,
        command: Command,
        event: MessageEvent,
        tag: str,
    ) -> tuple[list[BeatmapFile], str | None]:
        if command.kind in ("beatmap", "set"):
            assert self._session is not None and command.value is not None
            if command.kind == "beatmap":
                if command.payload is not None:
                    return [command.payload], None
                logger.info("%s 正在下载谱面 ID=%s …", tag, command.value)
                return [await osu_source.fetch_beatmap(self._session, self.cfg.osu, command.value)], None

            logger.info("%s 正在下载谱包 ID=%s …", tag, command.value)
            beatmaps = await osu_source.fetch_beatmapset(self._session, self.cfg.osu, command.value)
            limit = self.cfg.osu.max_beatmaps_per_pack
            logger.info("%s 谱包解析完成：共 %d 张谱面（上限 %d）", tag, len(beatmaps), limit)
            if len(beatmaps) > limit:
                raise OsuSourceError(
                    f"该谱包包含 {len(beatmaps)} 张谱面，超过单次上限 {limit} 张，请改用谱面 ID。"
                )
            return beatmaps, f"谱包共 {len(beatmaps)} 张谱面，正在分析…"

        if command.kind == "cid":
            assert command.value is not None
            try:
                _prepare_sibling_toolkit()
                try:
                    from nonebot_plugin_osumania_toolkit.api.malody import download_by_cid
                except ModuleNotFoundError:
                    from src.plugins.nonebot_plugin_osumania_toolkit.api.malody import download_by_cid
            except ImportError as exc:
                raise OsuSourceError("当前环境未安装同目录的 Malody 下载支持，无法使用 CID。") from exc
            cid_dir = self.cfg.storage.data_dir / "malody"
            cid_dir.mkdir(parents=True, exist_ok=True)
            downloaded = await download_by_cid(command.value, cid_dir)
            if downloaded is None or not downloaded.is_file():
                raise OsuSourceError(f"获取 Malody CID {command.value} 失败，请检查 CID 是否正确。")
            beatmaps = osu_source.read_local_beatmap(downloaded)
            if len(beatmaps) > 1:
                return beatmaps, f"CID {command.value} 共 {len(beatmaps)} 张谱面，正在分析…"
            return beatmaps, None

        # 回复文件
        logger.info("%s 解析回复消息中的附件…", tag)
        files = await self._files_from_reply(bot, event, tag)
        if not files:
            return [], None
        beatmaps: list[BeatmapFile] = []
        for filename, data in files:
            extracted = self._beatmaps_from_attachment(filename, data)
            logger.info("%s 附件 %s：%d 字节 -> %d 张谱面", tag, filename, len(data), len(extracted))
            beatmaps.extend(extracted)
        limit = self.cfg.osu.max_beatmaps_per_pack
        if len(beatmaps) > limit:
            raise OsuSourceError(f"文件包含 {len(beatmaps)} 张谱面，超过单次上限 {limit} 张。")
        ack = f"共 {len(beatmaps)} 张谱面，正在分析…" if len(beatmaps) > 1 else None
        return beatmaps, ack

    def _beatmaps_from_attachment(self, filename: str, data: bytes) -> list[BeatmapFile]:
        # 统一交给 archive：.osu / .mc / 任意 zip（.osz / .mcz / .zip，可递归嵌套）
        beatmaps = archive.collect(data, filename)
        for beatmap in beatmaps:
            if beatmap.error:
                logger.warning("  不可用：%s -> %s", beatmap.display_name, beatmap.error)
        return beatmaps

    async def _files_from_reply(
        self, bot: Bot, event: MessageEvent, tag: str
    ) -> list[tuple[str, bytes]]:
        reply = event.reply
        if reply is None:
            logger.info("%s 未检测到回复消息", tag)
            return []

        results: list[tuple[str, bytes]] = []
        for segment in reply.message:
            if segment.type != "file":
                continue
            payload = segment.data or {}
            filename = str(payload.get("file") or payload.get("name") or "attachment.osu")
            url = payload.get("url")
            if not url:
                file_id = payload.get("file_id") or payload.get("id")
                if file_id:
                    try:
                        if isinstance(event, GroupMessageEvent):
                            params: dict[str, Any] = {
                                "group_id": event.group_id,
                                "file_id": file_id,
                            }
                            if payload.get("busid") is not None:
                                try:
                                    params["busid"] = int(payload["busid"])
                                except (TypeError, ValueError):
                                    pass
                            fetched = await bot.call_api("get_group_file_url", **params)
                        else:
                            fetched = await bot.call_api(
                                "get_private_file_url",
                                user_id=event.user_id,
                                file_id=file_id,
                            )
                        if isinstance(fetched, dict):
                            url = fetched.get("url") or (fetched.get("data") or {}).get("url")
                    except Exception as exc:  # noqa: BLE001
                        logger.warning("%s 获取文件下载地址失败：%s", tag, exc)
            if not url:
                logger.warning("%s 附件 %s 没有可用的下载地址，已跳过", tag, filename)
                continue
            logger.info("%s 下载附件 %s …", tag, filename)
            data = await self._download(url)
            logger.info("%s 附件 %s 下载完成：%.1f KB", tag, filename, len(data) / 1024)
            results.append((filename, data))
        return results

    async def _download(self, url: str) -> bytes:
        if url.startswith("file://"):
            parsed = urlparse(url)
            raw_path = unquote(parsed.path)
            if parsed.netloc:
                raw_path = f"//{parsed.netloc}{raw_path}"
            return await asyncio.to_thread(Path(url2pathname(raw_path)).read_bytes)
        assert self._session is not None
        timeout = aiohttp.ClientTimeout(total=self.cfg.osu.timeout_sec)
        async with self._session.get(url, timeout=timeout) as resp:
            if resp.status != 200:
                raise OsuSourceError(f"下载附件失败（HTTP {resp.status}）。")
            return await resp.read()

    # ------------------------------------------------------------------ 渲染与发送
    async def _render_and_send(
        self,
        bot: Bot,
        event: MessageEvent,
        files: list[BeatmapFile],
        tag: str,
        command: Command,
    ) -> None:
        total = len(files)
        render_started = time.perf_counter()
        logger.info("%s 开始处理 %d 张谱面（串行渲染）", tag, total)

        items: list[ResultItem] = []
        hit_count = 0
        for index, beatmap_file in enumerate(files, 1):
            # 读取阶段就已判定不可用（例如当前转换器不支持的 Malody 格式）
            if beatmap_file.error:
                name = beatmap_file.display_name
                logger.info("%s [%d/%d] %s", tag, index, total, name)
                logger.warning("%s [%d/%d] 不可分析，改为错误提示：%s", tag, index, total, beatmap_file.error)
                items.append(
                    ResultItem(
                        name=name,
                        info=None,
                        error=beatmap_file.error,
                        png=render_error_card(beatmap_file.error, subtitle=name),
                    )
                )
                continue

            info = parse_beatmap(beatmap_file.text)
            name = info.display_name if info.title or info.artist else beatmap_file.display_name
            logger.info("%s [%d/%d] %s", tag, index, total, name)

            if not info.ok:
                logger.warning("%s [%d/%d] 不可分析，改为错误提示：%s", tag, index, total, info.error)
                items.append(
                    ResultItem(
                        name=name,
                        info=info,
                        error=info.error,
                        png=render_error_card(info.error or "无法分析该谱面。", subtitle=name),
                    )
                )
                continue

            background = beatmap_file.background if self.cfg.osu.include_background else None
            mode_labels = {0: "osu!standard", 1: "osu!taiko", 2: "osu!catch", 3: "osu!mania"}
            map_format = (
                f"{info.column_count}K mania"
                if info.mode == 3
                else mode_labels.get(info.mode, f"osu! mode {info.mode}")
            )
            logger.info(
                "%s [%d/%d] 校验通过：%s，背景图 %s",
                tag, index, total, map_format,
                f"{len(background) / 1024:.0f} KB" if background else "无",
            )
            item_started = time.perf_counter()
            render_variant = "|".join(
                [
                    f"rate:{command.speed_rate:.3f}",
                    f"mods:{','.join(command.mods)}",
                    f"od:{command.od_flag}",
                    f"cvt:{command.cvt_flag}",
                ]
            )
            chart_key = self.cache.key_for(beatmap_file.text, render_variant)
            cached = self.cache.get_image(chart_key)
            if cached is not None:
                png = cached
                hit_count += 1
                logger.info(
                    "%s [%d/%d] 命中历史结果（%s…），直接复用图片",
                    tag, index, total, chart_key[:12],
                )
            else:
                try:
                    png = await self.renderer.render(
                        beatmap_file.text,
                        background,
                        speed_rate=command.speed_rate,
                        od_flag=command.od_flag,
                        cvt_flag=command.cvt_flag,
                        mod_codes=command.mods,
                    )
                except RenderError as exc:
                    # 单张失败不影响同包其它谱面
                    logger.error("%s [%d/%d] 渲染失败，改为错误提示：%s", tag, index, total, exc)
                    message = f"渲染失败：{exc}"
                    items.append(
                        ResultItem(
                            name=name,
                            info=info,
                            error=message,
                            png=render_error_card(message, subtitle=name),
                        )
                    )
                    continue
                self.cache.put(
                    chart_key, beatmap_file.text, png,
                    name=name, source=beatmap_file.filename, background=background,
                )
                self._save_temp(name, png)
                logger.info(
                    "%s [%d/%d] 渲染并入库 %s…，用时 %.1fs（累计 %.1fs）",
                    tag, index, total, chart_key[:12],
                    time.perf_counter() - item_started,
                    time.perf_counter() - render_started,
                )
            items.append(ResultItem(name=name, info=info, png=png, key=chart_key))

        ok_count = sum(1 for item in items if not item.error)
        logger.info(
            "%s 渲染阶段结束：成功 %d 张 / 失败 %d 张，其中缓存命中 %d 张，用时 %.1fs",
            tag, ok_count, len(items) - ok_count, hit_count, time.perf_counter() - render_started,
        )

        if len(items) == 1:
            item = items[0]
            logger.info("%s 发送单张消息（%s）", tag, "错误提示" if item.error else "分析卡片")
            await bot.send(event, self._single_message(item))
            return

        nodes = [
            _forward_node(
                self.cfg.output.forward_name,
                self.cfg.output.forward_uin,
                [_text_node(f"Rework 分析结果 · 共 {len(items)} 张")],
            )
        ]
        sender = event.sender.card or event.sender.nickname or self.cfg.output.forward_name
        for item in items:
            content: list[dict[str, Any]] = [_text_node(item.name)]
            if item.png:
                content.append(_image_node(item.png))
            else:
                content.append(_text_node(item.error or "分析失败"))
            nodes.append(
                _forward_node(sender or self.cfg.output.forward_name, self.cfg.output.forward_uin, content)
            )

        if not self.cfg.output.send_as_forward:
            logger.info("%s 逐条发送 %d 张图片（合并转发已关闭）", tag, len(items))
            for item in items:
                await bot.send(event, self._single_message(item))
            return

        logger.info("%s 发送合并转发：%d 个节点…", tag, len(nodes))
        try:
            await self._send_forward(bot, event, nodes)
        except Exception as exc:  # noqa: BLE001
            logger.warning("%s 合并转发发送失败，改为逐条发送：%s", tag, exc)
            for item in items:
                await bot.send(event, self._single_message(item))
        else:
            logger.info("%s 合并转发已发送", tag)

    async def _send_forward(
        self,
        bot: Bot,
        event: MessageEvent,
        nodes: list[dict[str, Any]],
    ) -> None:
        if isinstance(event, GroupMessageEvent):
            attempts: list[tuple[str, dict[str, Any]]] = [
                ("send_group_forward_msg", {"group_id": event.group_id, "messages": nodes}),
                (
                    "send_forward_msg",
                    {"message_type": "group", "group_id": event.group_id, "messages": nodes},
                ),
            ]
        else:
            attempts = [
                ("send_private_forward_msg", {"user_id": event.user_id, "messages": nodes}),
                (
                    "send_forward_msg",
                    {"message_type": "private", "user_id": event.user_id, "messages": nodes},
                ),
            ]

        last_error: Exception | None = None
        for action, params in attempts:
            try:
                await bot.call_api(action, **params)
                return
            except Exception as exc:  # noqa: BLE001
                last_error = exc
        raise RuntimeError(f"所有合并转发接口均失败：{last_error}")

    def _single_message(self, item: ResultItem) -> Message:
        if item.error:
            message = Message(MessageSegment.text(f"❌ {item.name}\n{item.error}"))
            if item.png:
                message += MessageSegment.image(item.png)
            return message
        if item.png:
            return Message(MessageSegment.image(item.png))
        return Message(MessageSegment.text("分析失败"))

    def _temp_path(self, name: str, suffix: str) -> Path:
        safe = re.sub(r"[^\w\u4e00-\u9fff.\[\]()\-\s]+", "_", name)[:80] or "beatmap"
        return self.cfg.output.temp_dir / f"{safe}{suffix}"

    def _save_temp(self, name: str, png: bytes) -> None:
        if not self.cfg.output.keep_files:
            return
        try:
            path = self._temp_path(name, ".png")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(png)
        except OSError as exc:
            logger.debug("保存临时图片失败：%s", exc)
