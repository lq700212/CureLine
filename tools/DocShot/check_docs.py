# -*- coding: utf-8 -*-
"""
培训文档核验脚本（skill 第 ④ 步）。

用法：
    python tools/DocShot/check_docs.py          # 只查 md
    python tools/DocShot/check_docs.py --pdf    # md + PDF（>50KB 配对）

干什么：对 docs/ 下 6 份培训 md 逐份断言——
  ① 文件存在且 UTF-8 可读；
  ② H1 标题 = 常量表登记值；
  ③ 必含截图全部被引用；引用的图必须在白名单（防拼错名）；
  ④ 客户向 4 份：DEV_ONLY 图零出现 + 禁词零出现（权限红线）；
  ⑤ 内部版必须把对应程序的截图全部引用（全量口径）；
  ⑥ --pdf 时：docs/pdf/<同名>.pdf 存在且 >50KB。

输出：FAIL 明细逐条打印，全绿打 "check_docs: ALL PASS"。
退出码：0=全绿，1=有 FAIL。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_config as cfg

DOCS_DIR = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs"))
PDF_DIR = os.path.join(DOCS_DIR, "pdf")
PDF_MIN_BYTES = 50 * 1024

fails = []

def fail(doc, msg):
    fails.append(f"[{doc}] {msg}")

def check_md(name):
    path = os.path.join(DOCS_DIR, name)
    if not os.path.isfile(path):
        fail(name, "文件不存在")
        return None
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            text = f.read()
    except UnicodeDecodeError:
        fail(name, "不是 UTF-8（Windows 记事本另存 GBK 会炸中文）")
        return None

    # ② H1 收敛
    head = text.lstrip("").lstrip("\ufeff").splitlines()
    h1 = next((ln.strip() for ln in head if ln.strip().startswith("# ")), "")
    expect_h1 = cfg.DOC_TITLES[name]
    if h1 != expect_h1:
        fail(name, f"H1 不符：found {h1!r} expect {expect_h1!r}")

    refs = cfg.img_refs(text)

    # ③ 引用图必须白名单（防拼错名导致 md 渲染 404）
    for img in set(refs):
        if img not in cfg.IMG_WHITE:
            fail(name, f"引用了白名单外的图: {img}")

    # ④ 客户向：DEV_ONLY 图 + 禁词
    if name in cfg.CUSTOMER_DOCS:
        banned_imgs = [i for i in refs if i in cfg.DEV_ONLY_IMGS]
        if banned_imgs:
            fail(name, f"客户向文档出现 dev 专属图: {banned_imgs}")
        for word in cfg.CUSTOMER_BANNED:
            if word in text:
                fail(name, f"客户向文档出现禁词: {word!r}")

    # ⑤/{必备} 必含截图
    missing = [i for i in cfg.REQUIRED_IMGS[name] if i not in refs]
    if missing:
        fail(name, f"缺必含截图: {missing}")

    # 内部版全量断言（对应程序全部截图都要在场）
    if name == "固化程控_内部手册.md":
        need = [i for i in cfg.IMG_WHITE[:12] if i not in refs]
        if need:
            fail(name, f"内部版应含程序①全部截图，缺: {need}")
    if name == "整线网关_内部手册.md":
        need = [i for i in cfg.IMG_WHITE[12:] if i not in refs]
        if need:
            fail(name, f"内部版应含程序②全部截图，缺: {need}")
    return text

def check_pdf(name):
    pdf = os.path.join(PDF_DIR, name.replace(".md", ".pdf"))
    if not os.path.isfile(pdf):
        fail(name, "PDF 缺失（需先跑 md_to_pdf.py）")
        return
    size = os.path.getsize(pdf)
    if size <= PDF_MIN_BYTES:
        fail(name, f"PDF 仅 {size}B ≤ 50KB（疑似导空/截断）")

def main():
    want_pdf = "--pdf" in sys.argv
    for name in cfg.DOC_NAMES:
        check_md(name)
        if want_pdf:
            check_pdf(name)
    if fails:
        print(f"check_docs: {len(fails)} FAIL")
        for m in fails:
            print("  FAIL", m)
        sys.exit(1)
    print("check_docs: ALL PASS" + (" (md+pdf)" if want_pdf else " (md)"))

if __name__ == "__main__":
    main()
