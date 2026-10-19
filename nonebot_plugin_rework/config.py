"""插件配置。

NoneBot 通过 ``.env`` / ``.env.*`` 中的 ``REWORK_*`` 变量注入这里的扁平字段，
再用 :func:`build_runtime_config` 聚合成各模块使用的 dataclass。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field

PACKAGE_DIR = Path(__file__).resolve().parent
BUNDLED_PLUGIN_DIR = PACKAGE_DIR / "tosu_plugin"


class Config(BaseModel):
    """NoneBot 插件配置（全部字段均可在 .env 中用 REWORK_* 覆盖）。"""

    model_config = ConfigDict(extra="ignore")

    # 渲染
    rework_plugin_dir: str = ""
    rework_render_host: str = "127.0.0.1"
    rework_render_port: int = 24750
    rework_browser_channel: str = "msedge"
    rework_headless: bool = True
    rework_device_scale_factor: int = 2
    rework_page_timeout_ms: int = 180000
    rework_settle_ms: int = 1500

    # 谱面来源
    rework_beatmap_url: str = "https://osu.direct/api/osu/{id}"
    rework_beatmapset_url: str = "https://osu.direct/api/d/{id}"
    rework_user_agent: str = "nonebot-plugin-rework/1.0"
    rework_http_timeout_sec: float = 60.0
    rework_max_beatmaps_per_pack: int = 40
    rework_include_background: bool = True

    # 产物与缓存
    rework_data_dir: str = "data"
    rework_temp_dir: str = ""
    rework_keep_files: bool = False
    rework_cache_enabled: bool = True

    # 发送
    rework_send_as_forward: bool = True
    rework_forward_name: str = "Rework 谱面分析"
    rework_forward_uin: str = "10000"

    # 访问控制
    rework_allow_group: bool = True
    rework_allow_private: bool = True
    rework_group_allowlist: list[str] = Field(default_factory=list)
    rework_user_denylist: list[str] = Field(default_factory=list)


@dataclass
class RenderConfig:
    plugin_dir: Path
    host: str = "127.0.0.1"
    port: int = 24750
    browser_channel: str = "msedge"
    headless: bool = True
    device_scale_factor: int = 2
    page_timeout_ms: int = 180000
    settle_ms: int = 1500


@dataclass
class OsuConfig:
    beatmap_url: str = "https://osu.direct/api/osu/{id}"
    beatmapset_url: str = "https://osu.direct/api/d/{id}"
    user_agent: str = "nonebot-plugin-rework/1.0"
    timeout_sec: float = 60.0
    max_beatmaps_per_pack: int = 40
    include_background: bool = True


@dataclass
class OutputConfig:
    temp_dir: Path
    keep_files: bool = False
    send_as_forward: bool = True
    forward_name: str = "Rework 谱面分析"
    forward_uin: str = "10000"


@dataclass
class AccessConfig:
    allow_group: bool = True
    allow_private: bool = True
    group_allowlist: list[str] = field(default_factory=list)
    user_denylist: list[str] = field(default_factory=list)


@dataclass
class StorageConfig:
    data_dir: Path
    enable_cache: bool = True


@dataclass
class AppConfig:
    render: RenderConfig
    osu: OsuConfig
    output: OutputConfig
    access: AccessConfig
    storage: StorageConfig


def _resolve(base: Path, value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = base / path
    return path.resolve()


def build_runtime_config(config: Config) -> AppConfig:
    """把 NoneBot 配置转换为各模块使用的运行时配置。"""
    base = Path.cwd()
    plugin_dir = (
        _resolve(base, config.rework_plugin_dir)
        if config.rework_plugin_dir.strip()
        else BUNDLED_PLUGIN_DIR.resolve()
    )
    data_dir = _resolve(base, config.rework_data_dir or "data")
    temp_dir = (
        _resolve(base, config.rework_temp_dir)
        if config.rework_temp_dir.strip()
        else data_dir / "temp"
    )

    return AppConfig(
        render=RenderConfig(
            plugin_dir=plugin_dir,
            host=config.rework_render_host,
            port=int(config.rework_render_port),
            browser_channel=config.rework_browser_channel,
            headless=bool(config.rework_headless),
            device_scale_factor=max(1, int(config.rework_device_scale_factor)),
            page_timeout_ms=max(1000, int(config.rework_page_timeout_ms)),
            settle_ms=max(0, int(config.rework_settle_ms)),
        ),
        osu=OsuConfig(
            beatmap_url=config.rework_beatmap_url,
            beatmapset_url=config.rework_beatmapset_url,
            user_agent=config.rework_user_agent,
            timeout_sec=float(config.rework_http_timeout_sec),
            max_beatmaps_per_pack=max(1, int(config.rework_max_beatmaps_per_pack)),
            include_background=bool(config.rework_include_background),
        ),
        output=OutputConfig(
            temp_dir=temp_dir,
            keep_files=bool(config.rework_keep_files),
            send_as_forward=bool(config.rework_send_as_forward),
            forward_name=config.rework_forward_name,
            forward_uin=str(config.rework_forward_uin),
        ),
        access=AccessConfig(
            allow_group=bool(config.rework_allow_group),
            allow_private=bool(config.rework_allow_private),
            group_allowlist=[str(item) for item in config.rework_group_allowlist],
            user_denylist=[str(item) for item in config.rework_user_denylist],
        ),
        storage=StorageConfig(
            data_dir=data_dir,
            enable_cache=bool(config.rework_cache_enabled),
        ),
    )
