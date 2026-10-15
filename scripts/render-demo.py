#!/usr/bin/env python3
"""Render a replay GIF from the real capture. Requires Pillow; makes no model calls."""
import json
from pathlib import Path
import textwrap
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[1]
trace = json.loads((root / 'docs/demo-transcript.json').read_text())
font_paths = ['/System/Library/Fonts/Menlo.ttc', '/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf']
font_path = next((p for p in font_paths if Path(p).exists()), None)
font = ImageFont.truetype(font_path, 21) if font_path else ImageFont.load_default(size=21)
small = ImageFont.truetype(font_path, 16) if font_path else ImageFont.load_default(size=16)
width, height = 1120, 700
steps = [('You', trace['prompt'])]
for event in trace['events']:
    if event['type'] == 'tool_call':
        steps.append(('MCP tool call', event['name'].replace('mcp__business-data__', '') + '\n' + json.dumps(event['arguments'])))
steps.append(('Claude', trace['answer']))
frames = []
for stage in range(1, len(steps) + 1):
    frame = Image.new('RGB', (width, height), '#101827')
    draw = ImageDraw.Draw(frame)
    draw.rounded_rectangle((18, 16, width-18, height-16), radius=18, fill='#162235', outline='#30445e', width=2)
    draw.text((44, 36), 'Business Data MCP  /  Claude Code', font=font, fill='#f3f7fd')
    draw.text((44, 74), '.NET 8 + SQLite | real stdio tool calls | synthetic data', font=small, fill='#90b0ce')
    y = 122
    visible = steps[:stage]
    for speaker, content in visible:
        draw.text((44, y), speaker.upper(), font=small, fill='#67d9cc' if speaker != 'You' else '#e6b86a')
        y += 26
        for paragraph in content.replace('**', '').splitlines():
            for line in textwrap.wrap(paragraph, width=77) or ['']:
                if y > height-75:
                    break
                draw.text((44, y), line, font=font, fill='#e1eaf5')
                y += 28
        y += 18
    draw.text((44, height-48), 'Recorded conversation replay; delays condensed. No refunds or external services.', font=small, fill='#90b0ce')
    frames.append(frame)
frames[0].save(root / 'docs/demo.gif', save_all=True, append_images=frames[1:],
               duration=[2500]*(len(frames)-1)+[7000], loop=0, optimize=True)
frames[-1].save(root / 'docs/demo.png')
print('Rendered docs/demo.gif and docs/demo.png from the actual captured conversation.')
