import asyncio
from pathlib import Path
import aiohttp
import edge_tts

ROOT = Path(__file__).resolve().parent
LINES = (ROOT / "script.txt").read_text(encoding="utf-8").splitlines()

async def main():
    for i, text in enumerate(LINES):
        connector = aiohttp.TCPConnector(resolver=aiohttp.ThreadedResolver())
        try:
            await edge_tts.Communicate(text, "zh-CN-YunxiNeural", rate="+5%", connector=connector).save(
                str(ROOT / f"{i:02d}.mp3"), str(ROOT / f"{i:02d}.jsonl")
            )
            print(f"Narration {i + 1}/{len(LINES)} ready", flush=True)
        finally:
            await connector.close()

if __name__ == "__main__":
    asyncio.run(main())
