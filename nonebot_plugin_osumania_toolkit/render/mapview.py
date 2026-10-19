import asyncio
import base64
from pathlib import Path
import aiohttp
from playwright.async_api import async_playwright
from nonebot.log import logger
import jinja2
import random

# 背景图片路径（可根据需要修改）
_BG_IMAGE_PATH = Path(r"E:\Desktop\桌面图片\亚托莉7.png")

# 保留原有的渲染锁（如果你想保留并发控制，可继续使用；Playwright 自身是线程安全的，可不用锁）
_MAPVIEW_RENDER_LOCK = asyncio.Lock()
async def render_html_to_pic(html: str, viewport_width: int = 475, viewport_height: int = 490) -> bytes:
    """使用 Playwright 渲染 HTML 并截图返回 PNG 字节（自动适应卡片宽度）。"""
    async with async_playwright() as p:
        browser = await p.chromium.launch(
            args=[
                '--disable-gpu',
                '--disable-dev-shm-usage',
                '--no-sandbox',
                '--disable-setuid-sandbox',
            ]
        )
        # 先用较大宽度加载，保证内容完全渲染
        page = await browser.new_page(
            viewport={"width": 800, "height": viewport_height},
            device_scale_factor=1,
        )
        await page.set_content(html, wait_until="networkidle")

        # 获取主卡片实际宽度，动态调整视口
        card_element = await page.query_selector(".main-card")
        if card_element:
            bounding_box = await card_element.bounding_box()
            if bounding_box:
                # 视口宽度 = 卡片实际宽度 + 少量边距（避免阴影被裁切）
                new_width = int(bounding_box["width"]) + 8
                await page.set_viewport_size({"width": new_width, "height": viewport_height})
                await page.wait_for_timeout(100)  # 等待重排完成

        if card_element:
            screenshot = await card_element.screenshot(type="png")
        else:
            screenshot = await page.screenshot(full_page=False, type="png")
        await browser.close()
        return screenshot

async def _render_mapview_card_once(template_dir: Path, data: dict) -> bytes:
    """替代原有的 htmlkit 渲染函数，使用 Playwright 渲染。"""
    env = jinja2.Environment(
        loader=jinja2.FileSystemLoader(str(template_dir)),
        enable_async=True
    )
    template = env.get_template("mapview.html")
    html = await template.render_async(**data)
    return await render_html_to_pic(html)


async def render_analysis_card(TEMPLATE_DIR: Path, data: dict, user_id: int) -> bytes:
    # 兜底背景：优先用触发者QQ头像，失败则随机选bgs文件夹图片
    if not data.get("background_image"):
        bg_set = False
        # 1. 尝试QQ头像
        try:
            avatar_url = f"http://q1.qlogo.cn/g?b=qq&nk={user_id}&s=640"
            async with aiohttp.ClientSession(trust_env=True) as session:
                async with session.get(avatar_url, timeout=aiohttp.ClientTimeout(total=10)) as resp:
                    if resp.status == 200:
                        image_bytes = await resp.read()
                        b64 = base64.b64encode(image_bytes).decode("utf-8")
                        data["background_image"] = f"data:image/png;base64,{b64}"
                        bg_set = True
                        logger.debug(f"[QQ头像背景] 使用 {user_id} 的头像")
                    else:
                        logger.warning(f"[QQ头像背景] 下载失败，状态码 {resp.status}")
        except Exception as e:
            logger.error(f"[QQ头像背景] 获取头像失败: {e}")

        # 2. 回退到随机文件夹
        if not bg_set:
            bg_dir = Path(r"E:\Desktop\Baka666Bot\src\plugins\nonebot_plugin_osumania_toolkit\render\bgs")
            images = list(bg_dir.glob("*.jpg")) + list(bg_dir.glob("*.png"))
            if images:
                chosen = random.choice(images)
                try:
                    image_bytes = chosen.read_bytes()
                    b64 = base64.b64encode(image_bytes).decode("utf-8")
                    data["background_image"] = f"data:image/png;base64,{b64}"
                    logger.debug(f"[随机背景] 使用 {chosen.name}")
                except Exception as e:
                    logger.error(f"[随机背景] 编码失败: {e}")
            else:
                logger.warning("[随机背景] bgs 文件夹为空")

    async with _MAPVIEW_RENDER_LOCK:
        return await _render_mapview_card_once(TEMPLATE_DIR, data)
