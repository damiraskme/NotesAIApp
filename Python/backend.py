import json
import sys


def process(text: str) -> dict:
    words = len(text.split())
    chars = len(text.strip())
    lines = len([line for line in text.splitlines() if line.strip()])
    return {"message": f"{words} words, {chars} characters, {lines} lines"}


def upper(text: str) -> dict:
    return {"text": text.upper()}


ACTIONS = {
    "process": process,
    "upper": upper,
}


def main() -> None:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")

    try:
        request = json.loads(sys.stdin.read() or "{}")
        action = request.get("action", "process")
        handler = ACTIONS.get(action)
        if handler is None:
            result = {"error": f"{action}"}
        else:
            result = handler(request.get("text", ""))
    except Exception as exc: 
        result = {"error": str(exc)}

    json.dump(result, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
