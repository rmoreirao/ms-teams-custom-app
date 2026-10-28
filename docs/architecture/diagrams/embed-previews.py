"""Maintain GitHub-compatible references to the exported PNG diagrams."""

from pathlib import Path
import re


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


def main() -> None:
    text = DOCUMENT.read_text(encoding="utf-8")
    definitions = []
    for name in NAMES:
        if not (DIRECTORY / f"{name}.png").is_file():
            raise FileNotFoundError(f"Missing exported PNG diagram: {name}.png")
        inline = re.compile(rf"!\[([^\]]+)\]\(diagrams/{re.escape(name)}\.png\)")
        text, replacements = inline.subn(
            lambda match: f"![{match[1]}][preview-{name}]", text
        )
        if replacements != 1 and not (
            replacements == 0
            and len(re.findall(rf"!\[[^\]]+\]\[preview-{re.escape(name)}\]", text)) == 1
        ):
            raise ValueError(f"Expected exactly one image embed for {name}.")
        definitions.append(f"[preview-{name}]: diagrams/{name}.png")
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
    print(f"Linked {len(NAMES)} PNG previews in {DOCUMENT.name}.")
    print(f"Document size: {len(text.encode('utf-8')):,} bytes.")


if __name__ == "__main__":
    main()
