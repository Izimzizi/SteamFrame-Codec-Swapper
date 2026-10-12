"""Read-only Frame decoder clock/thermal snapshot; no settings are changed."""
import json
import pathlib

clocks = {}
for name in ('video_cc_mvs0_clk', 'video_cc_mvs0c_clk', 'video_cc_mvs1_clk', 'video_cc_mvs1c_clk'):
    path = pathlib.Path('/sys/kernel/debug/clk') / name / 'clk_rate'
    try:
        clocks[name] = int(path.read_text().strip())
    except (OSError, ValueError):
        clocks[name] = None
temperatures = []
for node in pathlib.Path('/sys/class/thermal').glob('thermal_zone*'):
    try:
        kind = (node / 'type').read_text().strip()
        if any(word in kind.lower() for word in ('video', 'vcodec', 'skin', 'cpu', 'soc', 'gpu')):
            temperatures.append({'sensor': kind, 'celsius': int((node / 'temp').read_text().strip()) / 1000})
    except (OSError, ValueError):
        pass
print(json.dumps({'clock_hz': clocks, 'temperatures': temperatures, 'settings_changed': False}))
