# -*- coding: utf-8 -*-
"""
md → PDF 转换（skill 第 ④ 步收尾）。

用法：python tools/DocShot/md_to_pdf.py [--only 文件名]

干什么：docs/ 下 6 份培训 md → docs/pdf/<同名>.pdf（A4、微软雅黑、
  图片表格全量渲染，print-color-adjust 保色）。

怎么干：
  1) python-markdown 转 HTML（tables/fenced_code 扩展，够本文档用）；
  2) HTML 落 docs/<md名>.html（与 images/ 同目录，相对路径可解析）；
  3) Edge headless --print-to-pdf 出 PDF（先 ASCII 临时名，成功后
     os.replace 到中文目标名——Edge 对中文输出路径偶发吞产物，双保险）；
  4) 产物 >50KB 才算数（约防"导出空文件"），HTML 临时件转完即删。

stderr 按字节收（Chromium DevTools 噪音混非 GBK 序列，终端直接打印会炸）。
"""

import os
import pathlib
import subprocess
import sys

import markdown

BASE = pathlib.Path(__file__).resolve().parent.parent.parent
DOCS = BASE / "docs"
PDF_DIR = DOCS / "pdf"

# Edge 位置探测（x86 在前——win32 下两处必有一处）
EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

CSS = """
<style>
@page { size: A4; margin: 16mm 14mm; }
body { font-family: "Microsoft YaHei", "Segoe UI", sans-serif; font-size: 10.5pt;
       line-height: 1.65; color: #22303f; -webkit-print-color-adjust: exact;
       print-color-adjust: exact; }
h1 { color: #1857c2; font-size: 20pt; border-bottom: 3px solid #1857c2;
     padding-bottom: 6px; margin: 0 0 18px; break-after: avoid; }
h2 { color: #1857c2; font-size: 15pt; border-bottom: 2px solid #d7e3f7;
     padding-bottom: 4px; margin: 22px 0 10px; break-after: avoid; }
h3 { color: #2c5aa0; font-size: 12.5pt; margin: 16px 0 8px; break-after: avoid; }
table { border-collapse: collapse; width: 100%; margin: 8px 0 14px; }
th, td { border: 1px solid #b9c4d3; padding: 4px 8px; font-size: 9.5pt;
         vertical-align: top; }
th { background: #eef3fb; color: #1b3f7a; }
img { max-width: 100%; border: 1px solid #d0d7e2; border-radius: 4px;
      margin: 8px 0 4px; }
pre { background: #f4f6fa; border: 1px solid #dfe5ee; border-radius: 4px;
      padding: 8px 10px; white-space: pre-wrap; word-break: break-all;
      font-size: 9.5pt; }
code { font-family: Consolas, monospace; background: #f4f6fa;
       padding: 0 3px; border-radius: 3px; }
blockquote { border-left: 4px solid #7aa7e8; background: #f2f7ff;
             margin: 8px 0; padding: 4px 12px; }
hr { border: none; border-top: 1px solid #ccd6e4; margin: 18px 0; }
ul, ol { margin: 4px 0 10px; padding-left: 22px; }
li { margin: 2px 0; }
strong { color: #b03030; }
</style>
"""

def md_to_html(md_path: pathlib.Path) -> str:
    """markdown → HTML 片段 + CSS（UTF-8-sig 读，容 BOM）。"""
    text = md_path.read_text(encoding="utf-8-sig")
    body = markdown.markdown(text, extensions=["tables", "fenced_code"])
    return ('<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">'
            + CSS + "</head><body>" + body + "</body></html>")

def find_edge():
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    raise FileNotFoundError("没找到 msedge.exe（两常见路径都缺）")

def convert(md_path: pathlib.Path, edge: str) -> pathlib.Path:
    """单份 md → PDF。返回目标 PDF 路径；失败抛异常（主循环收）。"""
    PDF_DIR.mkdir(exist_ok=True)
    html_path = DOCS / (md_path.stem + ".html")
    html_path.write_text(md_to_html(md_path), encoding="utf-8")

    tmp_pdf = PDF_DIR / "doc_tmp.pdf"
    target_pdf = PDF_DIR / (md_path.stem + ".pdf")
    url = html_path.as_uri()
    cmd = [
        edge,
        "--headless=new", "--disable-gpu", "--no-first-run",
        "--no-sandbox",
        f"--print-to-pdf={tmp_pdf}",
        "--print-to-pdf-no-header",
        url,
    ]
    # 字节收两路输出：DevTools 噪音混非 GBK 序列，直接文本化会 UnicodeDecodeError
    proc = subprocess.run(cmd, capture_output=True, timeout=120)
    noisy = (proc.stdout + proc.stderr).decode("utf-8", errors="replace")
    tail = noisy[-400:] if noisy else ""
    if not tmp_pdf.is_file():
        raise RuntimeError(f"Edge 未产出 PDF（rc={proc.returncode}）tail={tail!r}")
    tmp_bytes = tmp_pdf.read_bytes()
    tmp_pdf.unlink()
    html_path.unlink()
    if len(tmp_bytes) <= 50 * 1024:
        raise RuntimeError(f"PDF 仅 {len(tmp_bytes)}B（疑似空渲染）tail={tail!r}")
    target_pdf.write_bytes(tmp_bytes)
    return target_pdf

def main():
    only = None
    if "--only" in sys.argv:
        only = sys.argv[sys.argv.index("--only") + 1]
    edge = find_edge()
    sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
    import check_config as cfg
    names = cfg.DOC_NAMES if only is None else [only]
    bad = 0
    for name in names:
        md_path = DOCS / name
        if not md_path.is_file():
            print(f"SKIP {name}（md 不存在）")
            bad += 1
            continue
        try:
            pdf = convert(md_path, edge)
            print(f"OK   {name} -> {os.path.basename(pdf)} "
                  f"{os.path.getsize(pdf)}B")
        except Exception as ex:  # noqa: BLE001 转换失败要列明细，不让整批断
            print(f"FAIL {name}: {ex}")
            bad += 1
    if bad:
        print(f"md_to_pdf: {bad} FAIL")
        sys.exit(1)
    print("md_to_pdf: ALL DONE")

if __name__ == "__main__":
    main()

