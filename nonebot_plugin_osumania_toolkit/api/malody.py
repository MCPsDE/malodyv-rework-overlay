# api/malody.py — 通过 MalodyVClientPlugin API 下载谱面
import asyncio
import io
import zipfile
import sys
from pathlib import Path
from typing import Optional

import aiohttp
from nonebot.log import logger

CONCURRENCY = 4
TIMEOUT = 60

# 私服(后备)
_FALLBACK_DOWNLOAD_API = "http://api.paimonnokoya.me/api/store/download"
_FALLBACK_API_PARAMS = "api=202409&uid=1693832&key=B1j-KVQAzcfeQTiO7OEvidUiUI_odHyZQSEZ4W4ahvQWtnpjht5lUQolE05jiezZwH5_HtQaeqE31Gyr-0x4hJ4FgfjYrSfy-rYeAwkPmqnhLt7OnLqUl9xaWABVwWkJbHLAsyHcrfMReY9wdZRF_Pwwn_mBI8AUrAHW4GnuOiI"


async def _get_store_cred():
    """从 MalodyVClientPlugin 数据库获取任意已登录用户的 store_token"""
    try:
        from src.plugins.MalodyVClientPlugin.MalodyAPI.database import db
        import aiosqlite
        async with aiosqlite.connect(str(db.db_path)) as conn:
            cursor = await conn.execute(
                "SELECT uid, store_token FROM user_bind WHERE is_login=1 AND store_token IS NOT NULL LIMIT 1"
            )
            row = await cursor.fetchone()
            await cursor.close()
            if row:
                return str(row[0]), row[1]
    except Exception:
        pass
    return None, None


async def _download_via_official(cid: int) -> Optional[dict]:
    """通过官方 API 获取下载元数据"""
    uid, store_token = await _get_store_cred()
    if not store_token:
        return None
    try:
        from src.plugins.MalodyVClientPlugin.MalodyAPI.store.download import get_download
        import asyncio as aio
        return await aio.to_thread(get_download, store_token=store_token, uid=uid, cid=str(cid))
    except Exception as e:
        logger.warning(f"[Malody] 官方API获取CID {cid} 元数据失败: {e}")
        return None


async def _download_via_fallback(session: aiohttp.ClientSession, cid: int) -> Optional[dict]:
    """通过私服获取下载元数据(后备)"""
    import time
    ts = int(time.time())
    url = f"{_FALLBACK_DOWNLOAD_API}?{_FALLBACK_API_PARAMS}&cid={cid}&t={ts}"
    try:
        async with session.get(url, timeout=aiohttp.ClientTimeout(total=30)) as resp:
            data = await resp.json()
        return data
    except Exception as e:
        logger.warning(f"[Malody CID {cid}] 私服获取元数据失败: {e}")
        return None


async def download_by_cid(cid: int, cache_dir: Path) -> Optional[Path]:
    """根据 Malody CID 下载谱面文件，保存到 cache_dir，返回本地文件路径。"""
    connector = aiohttp.TCPConnector(limit=CONCURRENCY * 2)
    semaphore = asyncio.Semaphore(CONCURRENCY)

    async with aiohttp.ClientSession(
        connector=connector,
        trust_env=True,
    ) as session:
        # 1. 优先官方API，私服后备
        data = await _download_via_official(cid)
        if data is None:
            data = await _download_via_fallback(session, cid)

        if data is None or data.get("code") != 0 or not data.get("items"):
            logger.error(f"[Malody CID {cid}] 无法获取下载信息")
            return None

        items = data["items"]

        # 2. 并发下载所有文件
        async def download_item(item):
            file_url = item["file"]
            async with semaphore:
                try:
                    async with session.get(file_url, timeout=aiohttp.ClientTimeout(total=TIMEOUT)) as r:
                        if r.status == 200:
                            return item["name"], await r.read()
                except Exception as e:
                    logger.warning(f"[Malody] 下载 {item['name']} 失败: {e}")
            return item["name"], None

        tasks = [download_item(item) for item in items]
        results = await asyncio.gather(*tasks)

        valid = [(name, data) for name, data in results if data]
        if not valid:
            return None

        # 3. 保存为 .mc 或打包 .mcz
        if len(valid) == 1 and valid[0][0].lower().endswith(".mc"):
            name, data = valid[0]
            out_path = cache_dir / name
            out_path.write_bytes(data)
            return out_path

        out_path = cache_dir / f"malody_{cid}.mcz"
        with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED) as mcz:
            for name, data in valid:
                # 如果下载的文件本身是压缩包，则解包后存入
                try:
                    with zipfile.ZipFile(io.BytesIO(data)) as inner:
                        for info in inner.infolist():
                            if info.is_dir():
                                continue
                            mcz.writestr(info.filename, inner.read(info))
                except zipfile.BadZipFile:
                    mcz.writestr(name, data)
        return out_path
