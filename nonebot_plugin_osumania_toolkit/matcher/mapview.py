
import asyncio
import os
from pathlib import Path

from nonebot import logger, on_command
from nonebot.adapters.onebot.v11 import Bot, Message, MessageEvent, MessageSegment
from nonebot.exception import FinishedException

from ..algorithm.mapview import (
    analyze_mapview_chart,
    analyze_mapview_zip,
    format_mapview_result_text,
    format_parse_error_for_user,
)
from ..algorithm.pattern import PatternNotManiaError, PatternParseError
from ..algorithm.estimator.exceptions import ParseError, NotManiaError
from ..algorithm.utils import parse_cmd, send_forward_text_messages
from ..render.mapview import render_analysis_card
from ..api.download import download_file, get_file_url
from ..api.osu import download_file_by_id
from ..api.malody import download_by_cid
from ..file.path import safe_filename

from ..file.cache import CACHE_DIR
CACHE_DIR.mkdir(parents=True, exist_ok=True)
TEMPLATE_DIR = Path(__file__).resolve().parent.parent / "render" / "templates"


mapview = on_command("mapview", aliases={"rework2"}, block=True)


@mapview.handle()
async def handle_mapview(bot: Bot, event: MessageEvent):
    cmd_text = event.get_plaintext().strip()
    speed_rate, od_flag, cvt_flag, bid, mod_display, err_msg = parse_cmd(cmd_text)

    # 手动提取 Malody cid（c 开头 + 数字）
    cid = None
    for token in cmd_text.split():
        if token.lower().startswith("c") and token[1:].isdigit():
            cid = int(token[1:])
            break

    if err_msg:
        await mapview.finish("错误:\n" + "\n".join(err_msg) + "\n请检查命令格式并重试。")

    tmp_file: Path | None = None
    chart_file: Path | None = None

    try:
        # ---------- 分支 1：回复文件 ----------
        if event.reply:
            file_seg = None
            for seg in event.reply.message:
                if seg.type == "file":
                    file_seg = seg
                    break

            if not file_seg:
                await mapview.finish("回复的消息中没有找到文件。")

            file_info = await get_file_url(bot, file_seg)
            if not file_info:
                await mapview.finish("无法获取文件信息。请确保机器人有权限访问该文件，或者文件链接有效。")

            file_name, file_url = file_info
            file_name = safe_filename(os.path.basename(file_name))
            if not file_name.lower().endswith((".osu", ".mc", ".osz", ".mcz")):
                await mapview.finish("请回复 .osu/.mc/.osz/.mcz 格式的谱面文件。")

            tmp_file = CACHE_DIR / file_name
            await download_file(file_url, tmp_file)

            if file_name.lower().endswith((".osz", ".mcz")):
                rows, errors, total = await analyze_mapview_zip(
                    tmp_file, speed_rate, od_flag, cvt_flag, mod_display, CACHE_DIR
                )
                if not rows and not errors:
                    await mapview.finish("图包中没有可分析的谱面文件。")
                if not rows:
                    await mapview.finish("错误:\n" + "\n".join(errors))

                avalible = len(rows)
                if avalible <= 2:
                    # ≤2 张直接发送图片
                    for row in rows:
                        try:
                            image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                            await mapview.send(MessageSegment.image(image_bytes))
                        except Exception:
                            await mapview.send(format_mapview_result_text(row))
                    if errors:
                        await mapview.send("部分谱面分析失败:\n" + "\n".join(errors))
                    await mapview.finish()

                # 批量打包转发
                nodes: list[Message | str] = []
                batch_size = 5
                for idx, row in enumerate(rows):
                    try:
                        image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                        nodes.append(MessageSegment.image(image_bytes))
                    except Exception:
                        nodes.append(
                            f"{row['file_name']}:\n{format_mapview_result_text(row)}"
                        )
                    if len(nodes) >= batch_size or idx == avalible - 1:
                        await send_forward_text_messages(bot, event, nodes)
                        nodes = []
                    await asyncio.sleep(0.5)
                if errors:
                    await send_forward_text_messages(
                        bot, event, ["部分谱面分析失败:\n" + "\n".join(errors)]
                    )
                await mapview.finish()

            else:
                # 单文件 .osu/.mc
                chart_file = tmp_file

                row = await analyze_mapview_chart(
                    chart_file, file_name, speed_rate, od_flag, cvt_flag, mod_display, CACHE_DIR
                )
                try:
                    image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                    await mapview.finish(MessageSegment.image(image_bytes))
                except FinishedException:
                    raise
                except Exception:
                    await mapview.finish(format_mapview_result_text(row))

        # ---------- 分支 2：Malody CID ----------
        elif cid is not None:
            tmp_file = await download_by_cid(cid, CACHE_DIR)
            if not tmp_file:
                await mapview.finish("获取 Malody 谱面失败，请检查 CID 是否正确。")

            file_name = tmp_file.name
            if file_name.lower().endswith((".osz", ".mcz")):
                rows, errors, total = await analyze_mapview_zip(
                    tmp_file, speed_rate, od_flag, cvt_flag, mod_display, CACHE_DIR
                )
                if not rows and not errors:
                    await mapview.finish("图包中没有可分析的谱面文件。")
                if not rows:
                    await mapview.finish("错误:\n" + "\n".join(errors))

                avalible = len(rows)
                if avalible <= 2:
                    for row in rows:
                        try:
                            image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                            await mapview.send(MessageSegment.image(image_bytes))
                        except Exception:
                            await mapview.send(format_mapview_result_text(row))
                    if errors:
                        await mapview.send("部分谱面分析失败:\n" + "\n".join(errors))
                    await mapview.finish()

                nodes: list[Message | str] = []
                batch_size = 5
                for idx, row in enumerate(rows):
                    try:
                        image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                        nodes.append(MessageSegment.image(image_bytes))
                    except Exception:
                        nodes.append(
                            f"{row['file_name']}:\n{format_mapview_result_text(row)}"
                        )
                    if len(nodes) >= batch_size or idx == avalible - 1:
                        await send_forward_text_messages(bot, event, nodes)
                        nodes = []
                    await asyncio.sleep(0.5)
                if errors:
                    await send_forward_text_messages(
                        bot, event, ["部分谱面分析失败:\n" + "\n".join(errors)]
                    )
                await mapview.finish()
            else:
                # 单个 .mc 文件
                chart_file = tmp_file
                row = await analyze_mapview_chart(
                    chart_file, file_name, speed_rate, od_flag, cvt_flag, mod_display, CACHE_DIR
                )
                try:
                    image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                    await mapview.finish(MessageSegment.image(image_bytes))
                except FinishedException:
                    raise
                except Exception:
                    await mapview.finish(format_mapview_result_text(row))

        # ---------- 分支 3：osu! bid ----------
        elif bid is not None:
            tmp_file, file_name = await download_file_by_id(CACHE_DIR, bid)
            chart_file = tmp_file

            row = await analyze_mapview_chart(
                chart_file, file_name, speed_rate, od_flag, cvt_flag, mod_display, CACHE_DIR
            )
            try:
                image_bytes = await render_analysis_card(TEMPLATE_DIR, row["template"], event.user_id)
                await mapview.finish(MessageSegment.image(image_bytes))
            except FinishedException:
                raise
            except Exception:
                await mapview.finish(format_mapview_result_text(row))

        else:
            # 没有提供任何有效输入
            await mapview.finish("请回复包含 .osu/.mc/.osz/.mcz 文件的消息，或使用 bid/mania 谱面网址指定谱面，也可以使用 Malody cid。")

    except FinishedException:
        raise
    except (ParseError, PatternParseError) as e:
        await mapview.finish(format_parse_error_for_user(e))
    except (NotManiaError, PatternNotManiaError):
        await mapview.finish("该谱面不是 mania 模式，无法分析。")
    except Exception as e:
        logger.exception(f"[mapview] 分析失败: {type(e).__name__}: {e}")
        error_text = str(e)
        if "超过" in error_text or "过大" in error_text:
            await mapview.finish(
                f"分析失败：{e}\n"
                "建议：可以删除图包内的媒体文件（音频/背景视频/图片）后再重新打包上传。"
            )
        elif "max() iterable argument is empty" in error_text:
            await mapview.finish(f"错误: 未找到谱面 b{bid}，请检查bid是否正确")
        else:
            await mapview.finish(f"分析失败：请反馈bot主人")
    finally:
        if tmp_file and tmp_file.exists():
            tmp_file.unlink()
        if chart_file and chart_file != tmp_file and chart_file.exists():
            chart_file.unlink()
