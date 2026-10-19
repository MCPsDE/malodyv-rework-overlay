"""``/rework2`` 指令处理器与服务生命周期。"""

from __future__ import annotations

import asyncio
import logging

from nonebot import get_driver, get_plugin_config, on_command
from nonebot.adapters.onebot.v11 import Bot, Message, MessageEvent
from nonebot.params import CommandArg
from nonebot.rule import Rule

from .config import AppConfig, Config, build_runtime_config
from .renderer import Renderer
from .service import ReworkService, is_allowed

logger = logging.getLogger(__name__)

_runtime: AppConfig | None = None
_service: ReworkService | None = None
_renderer: Renderer | None = None
_init_lock = asyncio.Lock()


def get_runtime_config() -> AppConfig:
    global _runtime
    if _runtime is None:
        _runtime = build_runtime_config(get_plugin_config(Config))
    return _runtime


def _allowed(event: MessageEvent) -> bool:
    return is_allowed(get_runtime_config(), event)


async def get_service() -> ReworkService:
    """首次使用时启动静态服务与浏览器，之后复用同一实例。"""
    global _service, _renderer
    async with _init_lock:
        if _service is None:
            cfg = get_runtime_config()
            if not cfg.render.plugin_dir.is_dir():
                raise RuntimeError(
                    f"找不到渲染插件目录：{cfg.render.plugin_dir}\n"
                    "请检查 REWORK_PLUGIN_DIR，或使用仓库内自带的 tosu_plugin。"
                )
            if not (cfg.render.plugin_dir / "settings.json").is_file():
                raise RuntimeError(
                    f"渲染插件目录缺少 settings.json：{cfg.render.plugin_dir}"
                )
            renderer = Renderer(cfg.render)
            await renderer.start()
            service = ReworkService(cfg, renderer)
            await service.start()
            _renderer = renderer
            _service = service
        return _service


async def close_service() -> None:
    global _service, _renderer
    async with _init_lock:
        if _service is not None:
            await _service.close()
            _service = None
        if _renderer is not None:
            await _renderer.close()
            _renderer = None


driver = get_driver()


@driver.on_shutdown
async def _shutdown() -> None:
    await close_service()


rework2 = on_command("rework", rule=Rule(_allowed), priority=1, block=True)


@rework2.handle()
async def _handle_rework2(
    bot: Bot,
    event: MessageEvent,
    args: Message = CommandArg(),
) -> None:
    try:
        service = await get_service()
    except Exception as exc:  # noqa: BLE001
        logger.exception("初始化 Rework 渲染服务失败")
        await bot.send(event, f"Rework 服务初始化失败：{exc}")
        return
    await service.handle(bot, event, args.extract_plain_text())
