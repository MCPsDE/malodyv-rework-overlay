from pathlib import Path
from typing import Optional
from nonebot import logger
import base64, aiohttp, os, json

OSU_ASSET_BASE = "https://assets.ppy.sh/beatmaps"

async def url_to_base64(url: str) -> Optional[str]:
    """将 file:// 或 http(s) 背景图转为 base64 data URI，失败返回 None"""
    try:
        if url.startswith("file://"):
            path_str = url[7:]
            if os.name == "nt" and path_str.startswith("/"):
                path_str = path_str[1:]
            file_path = Path(path_str)
            if file_path.exists():
                data = file_path.read_bytes()
                ext = file_path.suffix.lower()
                mime = "image/png" if ext == ".png" else "image/jpeg"
                b64 = base64.b64encode(data).decode("utf-8")
                return f"data:{mime};base64,{b64}"
        elif url.startswith(("http://", "https://")):
            async with aiohttp.ClientSession(trust_env=True) as session:
                async with session.get(url) as resp:
                    if resp.status == 200:
                        data = await resp.read()
                        mime = resp.content_type or "image/jpeg"
                        b64 = base64.b64encode(data).decode("utf-8")
                        return f"data:{mime};base64,{b64}"
    except Exception:
        pass
    return None


def get_background_from_osu_file(osu_path: Path) -> Optional[str]:
    """从 .osu 文件的 [Events] 段提取背景文件名"""
    try:
        with open(osu_path, encoding="utf-8", errors="ignore") as f:
            for line in f:
                line = line.strip()
                if line.startswith("0,0,\"") and line.endswith('",0,0'):
                    return line.split('"')[1]
                elif line.startswith("0,0,\""):
                    parts = line.split('"')
                    if len(parts) >= 3:
                        return parts[1]
    except Exception:
        pass
    return None


def get_background_from_mc_meta(mc_content: str) -> Optional[str]:
    """从 .mc 文件的 JSON 内容中提取背景文件名（meta.background）"""
    try:
        data = json.loads(mc_content)
        return data.get("meta", {}).get("background")
    except Exception:
        pass
    return None


def build_osu_background_url(beatmapset_id: int, filename: str) -> str:
    return f"{OSU_ASSET_BASE}/{beatmapset_id}/{filename}"


def _find_file_recursive(directory: Path, filename: str) -> Optional[Path]:
    """在目录及其所有子目录中查找指定文件（不区分大小写）"""
    filename_lower = filename.lower()
    for f in directory.rglob("*"):
        if f.is_file() and f.name.lower() == filename_lower:
            return f
    return None


async def get_background_url(
    *,
    bid: Optional[int] = None,
    bmsid: Optional[int] = None,
    local_osu: Optional[Path] = None,
    mcz_meta: Optional[dict] = None,
) -> str:
    """
    返回背景图 URL（file:// 或 https://）。
    优先级：本地文件 > 官方封面图（基于 bmsid） > 空
    """
    if local_osu and local_osu.exists():
        # ---------- 处理 .osu 文件 ----------
        if local_osu.suffix.lower() == ".osu":
            bg_filename = get_background_from_osu_file(local_osu)
            if bg_filename:
                candidate = local_osu.parent / bg_filename
                if candidate.exists():
                    return candidate.as_uri()
                found = _find_file_recursive(local_osu.parent, bg_filename)
                if found:
                    return found.as_uri()
                if bmsid:
                    logger.info(f"[背景] 本地未找到 {bg_filename}，使用封面图")
                    return f"{OSU_ASSET_BASE}/{bmsid}/covers/cover.jpg"
            if bmsid:
                logger.info("[背景] 无背景文件名，使用封面图")
                return f"{OSU_ASSET_BASE}/{bmsid}/covers/cover.jpg"

        # ---------- 处理 .mc 文件 (Malody) ----------
        elif local_osu.suffix.lower() == ".mc":
            try:
                mc_content = local_osu.read_text(encoding="utf-8")
                bg_filename = get_background_from_mc_meta(mc_content)
                # 先尝试本地查找
                if bg_filename:
                    candidate = local_osu.parent / bg_filename
                    if candidate.exists():
                        logger.success(f"[背景] 本地直接找到: {candidate}")
                        return candidate.as_uri()
                    found = _find_file_recursive(local_osu.parent, bg_filename)
                    if found:
                        logger.success(f"[背景] 递归搜索找到: {found}")
                        return found.as_uri()
                    logger.info(f"[背景] 本地未找到 {bg_filename}，尝试网络获取")
                # 网络后备：使用 Malody 封面 API
                data = json.loads(mc_content)
                sid = data.get("meta", {}).get("id")
                if sid:
                    logger.info(f"[背景] 使用 Malody 封面 API，sid={sid}")
                    return f"https://cni.machart.top/cover/{sid}"
            except Exception as e:
                logger.warning(f"[背景] 解析 .mc 背景失败: {e}")

    # 纯在线 bmsid（无本地文件）
    if bmsid:
        logger.info("[背景] 纯在线 bid，使用封面图")
        return f"{OSU_ASSET_BASE}/{bmsid}/covers/cover.jpg"

    return ""
