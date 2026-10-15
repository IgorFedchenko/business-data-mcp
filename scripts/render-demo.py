#!/usr/bin/env python3
"""Paginated replay from a genuine validated capture; makes no model calls."""

import hashlib
import json
from pathlib import Path
import textwrap
from PIL import Image, ImageDraw, ImageFont
from eval_contract import ROOT, check_answer, check_capture


def main():
    trace = json.loads((ROOT / "docs/demo-transcript.json").read_text())
    check_capture(trace)
    assert hashlib.sha256(trace["answer"].encode()).hexdigest() == trace["answerSha256"]
    check_answer(json.loads(trace["answer"]))
    font_paths = ["/System/Library/Fonts/Menlo.ttc", "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"]
    font_path = next((p for p in font_paths if Path(p).exists()), None)
    font = ImageFont.truetype(font_path, 20) if font_path else ImageFont.load_default(size=20)
    width, height = 1120, 900
    steps = [("PROMPT", trace["prompt"]), ("CLAUDE — ORIGINAL COMPLETE ANSWER", trace["answer"])]
    for event in trace["events"]:
        if event["type"] == "tool_call":
            steps.append(("ACTUAL READ TOOL CALL", event["name"] + "\n" + json.dumps(event["arguments"])))
    steps.append(("SERVER AUDIT", json.dumps(trace["serverAudit"], indent=2)))
    frames = []
    for speaker, content in steps:
        lines = [
            line
            for paragraph in content.splitlines()
            for line in (textwrap.wrap(paragraph, width=82) or [""])
        ]
        for offset in range(0, max(1, len(lines)), 23):
            frame = Image.new("RGB", (width, height), "#101827")
            draw = ImageDraw.Draw(frame)
            draw.text((36, 30), "Business Data MCP | Claude read-only assistant", font=font, fill="#f3f7fd")
            draw.text(
                (36, 78),
                speaker + f"  ({offset // 23 + 1}/{max(1, (len(lines) + 22) // 23)})",
                font=font,
                fill="#67d9cc",
            )
            for index, line in enumerate(lines[offset : offset + 23]):
                draw.text((36, 130 + index * 29), line, font=font, fill="#e1eaf5")
            draw.text(
                (36, 840),
                "Actual conversation replay; delays condensed. Synthetic data. No writes.",
                font=font,
                fill="#90b0ce",
            )
            frames.append(frame)
    frames[0].save(ROOT / "docs/demo.gif", save_all=True, append_images=frames[1:], duration=6000, loop=0)
    frames[1].save(ROOT / "docs/demo.png")
    print("Rendered complete paginated genuine read-only replay. Deterministic write demo is separate.")


if __name__ == "__main__":
    main()
