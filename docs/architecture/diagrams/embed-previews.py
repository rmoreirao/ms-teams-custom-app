"""Embed portable PNG previews while retaining separate high-resolution assets."""

from base64 import b64encode
from io import BytesIO
from pathlib import Path
import re

from PIL import Image


DIRECTORY = Path(__file__).resolve().parent
DOCUMENT = DIRECTORY.parent / "event-driven-poc.md"
BEGIN = "<!-- BEGIN GENERATED DIAGRAM PREVIEWS -->"
END = "<!-- END GENERATED DIAGRAM PREVIEWS -->"
NAMES = (
    "azure-resource-overview",
    "write-path",
    "send-operation-status",
    "sender-to-recipient",
    "teams-event-ingestion",
    "change-feed-delivery",
    "read-path",
    "projection-flow",
    "projector-sequence",
    "cold-view-hydration",
)


def preview(name: str) -> str:
    with Image.open(DIRECTORY / f"{name}.png") as source:
        image = source.convert("RGB")
        image.thumbnail((1601, 901), Image.Resampling.LANCZOS)
        image = image.quantize(colors=256, dither=Image.Dither.NONE)
        data = BytesIO()
        image.save(data, format="PNG", optimize=True)
    return "data:image/png;base64," + b64encode(data.getvalue()).decode("ascii")


def main() -> None:
    text = DOCUMENT.read_text(encoding="utf-8")
    definitions = []
    for name in NAMES:
        inline = re.compile(rf"!\[([^\]]+)\]\(diagrams/{re.escape(name)}\.png\)")
        text, replacements = inline.subn(
            lambda match: f"![{match[1]}][preview-{name}]", text
        )
        if replacements != 1 and not (
            replacements == 0
            and len(re.findall(rf"!\[[^\]]+\]\[preview-{re.escape(name)}\]", text)) == 1
        ):
            raise ValueError(f"Expected exactly one image embed for {name}.")
        definitions.append(f"[preview-{name}]: {preview(name)}")
    block = BEGIN + "\n" + "\n".join(definitions) + "\n" + END
    if BEGIN in text or END in text:
        if text.count(BEGIN) != 1 or text.count(END) != 1:
            raise ValueError("Generated preview markers must occur exactly once.")
        pattern = re.compile(re.escape(BEGIN) + r".*?" + re.escape(END), re.DOTALL)
        text, replacements = pattern.subn(lambda _: block, text)
        if replacements != 1:
            raise ValueError("Generated preview markers are out of order.")
    else:
        text = text.rstrip() + "\n\n" + block + "\n"
    DOCUMENT.write_text(text, encoding="utf-8", newline="\n")
    print(f"Embedded {len(NAMES)} self-contained PNG previews in {DOCUMENT.name}.")
    print(f"Document size: {len(text.encode('utf-8')):,} bytes.")


if __name__ == "__main__":
    main()
