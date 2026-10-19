import asyncio
import httpx

# 你的 osu! API 地址和 key（根据实际配置修改）
OSU_API_BASE = "https://osu.ppy.sh/api/v2"        # 或你自建的代理
ACCESS_TOKEN = "your_access_token"                # 从 nonebot 配置中取

async def osu_api(endpoint: str, **params):
    """模拟你项目中的 osu_api，返回 JSON"""
    headers = {"Authorization": f"Bearer {ACCESS_TOKEN}"}
    url = f"{OSU_API_BASE}/{endpoint}"
    async with httpx.AsyncClient() as client:
        resp = await client.get(url, headers=headers, params=params)
        resp.raise_for_status()
        return resp.json()

async def main():
    map_id = 3549424   # 你样例里的 bid
    try:
        data = await osu_api("beatmaps/lookup", id=map_id)
        # 如果是 osu! api v2，返回的结构可能不同，这里按常见格式打印
        import json
        print(json.dumps(data, indent=2, ensure_ascii=False))
    except Exception as e:
        print(f"请求失败: {e}")

if __name__ == "__main__":
    asyncio.run(main())