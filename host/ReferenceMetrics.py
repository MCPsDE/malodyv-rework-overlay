import json
import pathlib
import sys
import tempfile
import types

toolkit = pathlib.Path(sys.argv[1]).resolve()
speed_rate = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
if toolkit.name != "nonebot_plugin_osumania_toolkit":
    toolkit = toolkit / "nonebot_plugin_osumania_toolkit"
sys.path.insert(0, str(toolkit.parent))
packages = {
    "nonebot_plugin_osumania_toolkit": toolkit,
    "nonebot_plugin_osumania_toolkit.algorithm": toolkit / "algorithm",
    "nonebot_plugin_osumania_toolkit.algorithm.estimator": toolkit / "algorithm" / "estimator",
    "nonebot_plugin_osumania_toolkit.algorithm.rework": toolkit / "algorithm" / "rework",
    "nonebot_plugin_osumania_toolkit.parser": toolkit / "parser",
    "nonebot_plugin_osumania_toolkit.data": toolkit / "data",
}
for name, package_path in packages.items():
    module = sys.modules.get(name)
    if module is None:
        module = types.ModuleType(name)
        module.__package__ = name
        module.__path__ = [str(package_path)]
        sys.modules[name] = module

from nonebot_plugin_osumania_toolkit.algorithm.estimator.sunny import estimate_sunny_result
from nonebot_plugin_osumania_toolkit.algorithm.official import calculate_official_sr

osu_text = sys.stdin.buffer.read().decode("utf-8-sig", errors="replace")
temporary_path = None
sunny_star = None
sunny_est_diff = None
official_star = None
try:
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", suffix=".osu", delete=False) as file:
        file.write(osu_text)
        temporary_path = pathlib.Path(file.name)
    try:
        result = estimate_sunny_result(str(temporary_path), speed_rate, None, None)
        sunny_star = float(result["star"])
        sunny_est_diff = str(result["estDiff"])
    except Exception as exc:
        print(f"Sunny reference estimate unavailable: {exc}", file=sys.stderr)
    try:
        value = float(calculate_official_sr(str(temporary_path), speed_rate))
        official_star = value if value > 0 else None
    except Exception as exc:
        print(f"Official SR estimate unavailable: {exc}", file=sys.stderr)
finally:
    if temporary_path is not None:
        temporary_path.unlink(missing_ok=True)

print(json.dumps({"sunnyRework": sunny_star, "sunnyEstDiff": sunny_est_diff, "osuSr": official_star}))
