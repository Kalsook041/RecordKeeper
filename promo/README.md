# ReplayKeeper 宣传片

32 秒，1920×1080，30 fps，H.264 / AAC MP4。黑绿品牌设计、六段动效、中文神经语音旁白、原创合成电子配乐与内嵌字幕。

- `ReplayKeeper-promo.mp4`：完整宣传片。
- `poster.jpg`：视频封面。
- `ReplayKeeper-promo.srt`：可编辑的旁白字幕。
- `STORYBOARD.md`：分镜与文案。
- `render.py`：动画和原创配乐的渲染源代码。
- `narration/script.txt`：配音文案；`narration/generate.py` 使用 `zh-CN-YunxiNeural` 生成配音。
- `status-window.png`：从程序原始界面代码渲染的当前真实状态快照，含恢复次数 1。

自动恢复部分是压缩时间的动效流程示意。真实状态界面采用制作时的状态快照，不是实时录屏。制作期间日志记录了两次恢复尝试，并于第二次尝试后的复查确认回放开关恢复开启。

音乐、提示音和动态图形由本项目脚本生成。品牌图标沿用 `assets/replaykeeper.png`；中文字体使用本机 Microsoft YaHei。没有使用第三方游戏录像；成片与渲染源码随项目仓库提供。

## 重新渲染

环境：Windows、Python、Pillow、NumPy、FFmpeg。重新生成旁白还需要 `edge-tts`、`aiohttp` 与网络。

```powershell
python .\promo\narration\generate.py
.\promo\capture-status.ps1
python .\promo\render.py
```

只导出分镜预览和配乐时，运行 `python .\promo\render.py --preview`。
