# -*- coding: utf-8 -*-
"""把「星露谷新手图文教程.html」解析为结构化 JSON，供游戏内 Mod 渲染。

输出：assets/guide.json
    { "title": ..., "chapters": [ { "id","title","sub","blocks":[ ... ] } ] }

block 类型：
    h3 / h4 / p / cap / note(k) / li(o) / flow(xs) / tbl(rows) / box(title,xs)
"""
import json, os, re
from html.parser import HTMLParser

SRC = os.path.join(os.path.dirname(__file__), "..", "_input", "星露谷新手图文教程.html")
OUT = os.path.join(os.path.dirname(__file__), "..", "assets", "guide.json")

VOID = {"br", "img", "hr", "meta", "link", "input", "source", "path", "circle", "rect", "line", "text", "polygon"}
# 交互控件 / 纯装饰，游戏内由 Mod 自己实现，不搬进正文
SKIP_ID = {"eq", "estat", "eonlyO", "eclr", "etypes", "egrid", "enone"}
SKIP_CLS = {"blob", "foot", "toc", "wheel-box", "ehint", "mine"}


class Node:
    __slots__ = ("tag", "attrs", "children", "text")

    def __init__(self, tag, attrs=None):
        self.tag = tag
        self.attrs = attrs or {}
        self.children = []
        self.text = ""

    def cls(self):
        return self.attrs.get("class", "").split()

    def has(self, c):
        return c in self.cls()


class P(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.root = Node("#root")
        self.stack = [self.root]

    def handle_starttag(self, tag, attrs):
        n = Node(tag, {k: (v or "") for k, v in attrs})
        self.stack[-1].children.append(n)
        if tag not in VOID:
            self.stack.append(n)

    def handle_startendtag(self, tag, attrs):
        self.stack[-1].children.append(Node(tag, {k: (v or "") for k, v in attrs}))

    def handle_endtag(self, tag):
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag == tag:
                del self.stack[i:]
                break

    def handle_data(self, d):
        # 用文本节点保持文档顺序（否则「<span>1</span>标题」会变成「标题1」）
        n = Node("#text")
        n.text = d
        self.stack[-1].children.append(n)


def txt(node):
    parts = [node.text]
    for c in node.children:
        parts.append(txt(c))
    return re.sub(r"\s+", " ", "".join(parts)).strip()


def norm(s):
    return re.sub(r"\s+", " ", s or "").strip()


def table_rows(t):
    rows = []
    for tr in t.children:
        if tr.tag != "tr":
            continue
        cells = []
        for c in tr.children:
            if c.tag in ("td", "th"):
                cells.append(norm(txt(c)))
        if cells:
            rows.append(cells)
    # thead / tbody 包了一层
    if not rows:
        for sec in t.children:
            if sec.tag in ("thead", "tbody", "tfoot"):
                rows.extend(table_rows(sec))
    return rows


def note_kind(n):
    for k in ("tip", "warn", "info"):
        if n.has(k):
            return k
    return ""


def emit(node, out, depth=0):
    """把节点转成 block 列表，追加到 out。"""
    if isinstance(node, str):
        return
    tag = node.tag
    cls = node.cls()

    if tag in ("script", "style"):
        return
    if node.attrs.get("id") in SKIP_ID:
        return
    if any(c in SKIP_CLS for c in cls):
        return

    # 标题
    if tag in ("h3", "h4"):
        t = norm(txt(node))
        if t:
            out.append({"t": "h4" if tag == "h4" else "h3", "x": t})
        return

    if tag == "h2":
        return

    # 段落
    if tag == "p":
        t = norm(txt(node))
        if not t:
            return
        if node.has("sub"):
            return  # 章节副标题单独处理
        out.append({"t": "cap" if node.has("cap") else "p", "x": t})
        return

    # 提示框
    if node.has("note"):
        t = norm(txt(node))
        if t:
            out.append({"t": "note", "k": note_kind(node), "x": t})
        return

    # 流程箭头
    if node.has("flow"):
        xs = [norm(txt(c)) for c in node.children if c.has("node")]
        if xs:
            out.append({"t": "flow", "xs": xs})
        return

    # 列表
    if tag in ("ul", "ol"):
        ordered = tag == "ol"
        for li in node.children:
            if li.tag != "li":
                continue
            t = norm(txt(li))
            if t:
                out.append({"t": "li", "o": ordered, "x": t})
        return

    # 表格
    if tag == "table":
        rows = table_rows(node)
        if rows:
            out.append({"t": "tbl", "rows": rows})
        return

    # 卡片：mini / way / school / tl-item
    if node.has("mini") or node.has("way") or node.has("school") or node.has("tl-item"):
        title, xs = "", []
        for c in node.children:
            if c.tag in ("h4",):
                title = norm(txt(c))
            elif c.has("wt"):
                title = norm(txt(c))
            elif c.has("d") or c.has("h"):
                xs.append(norm(txt(c)))
            elif c.tag == "p":
                xs.append(norm(txt(c)))
            elif c.has("kv"):
                xs.append(norm(txt(c)))
            else:
                s = norm(txt(c))
                if s and s != title:
                    xs.append(s)
        if title or xs:
            out.append({"t": "box", "title": title, "xs": xs})
        return

    # 社区中心房间
    if node.has("room"):
        for c in node.children:
            if c.has("room-head"):
                out.append({"t": "h3", "x": norm(txt(c))})
            else:
                for b in c.children:
                    if b.has("bundle"):
                        for bb in b.children:
                            if bb.has("bt"):
                                out.append({"t": "h4", "x": norm(txt(bb))})
                            elif bb.tag == "table":
                                emit(bb, out)
                    elif b.tag == "table":
                        emit(b, out)
                    else:
                        emit(b, out)
        return

    # ID 速查小标题（.idh）
    if node.has("idh"):
        out.append({"t": "h4", "x": norm(txt(node))})
        return

    # 心数 / 图例等纯装饰
    if node.has("hearts"):
        t = norm(txt(node))
        if t:
            out.append({"t": "p", "x": t})
        return

    # 容器：递归
    for c in node.children:
        emit(c, out, depth + 1)

    # 容器自身直接带文本（没有被子标签包住）
    if depth > 0 and not [c for c in node.children if c.tag != "#text"]:
        t = norm(txt(node))
        if t and (not out or out[-1].get("x") != t):
            out.append({"t": "p", "x": t})


def main():
    src = os.path.abspath(SRC)
    with open(src, "r", encoding="utf-8") as f:
        html = f.read()

    p = P()
    p.feed(html)

    root = p.root
    title = ""
    chapters = []

    def walk(n):
        for c in n.children:
            if c.tag == "title":
                pass
            if c.tag == "section" and c.has("card"):
                cid = c.attrs.get("id", "")
                ctitle, csub = "", ""
                blocks = []
                for x in c.children:
                    if x.tag == "h2":
                        ctitle = norm(txt(x))
                        # 去掉序号「1」
                        ctitle = re.sub(r"^\d+\s*", "", ctitle)
                    elif x.tag == "p" and x.has("sub"):
                        csub = norm(txt(x))
                    else:
                        emit(x, blocks)
                chapters.append({"id": cid, "title": ctitle, "sub": csub, "blocks": blocks})
            else:
                walk(c)

    # 取 <title>
    m = re.search(r"<title>(.*?)</title>", html, re.S)
    if m:
        title = norm(m.group(1))

    walk(root)

    # 第 11 章原本的内嵌查询器已由 Mod 的「物品查询」页取代，补一句指引
    for ch in chapters:
        if ch["id"] == "c11":
            ch["blocks"].insert(0, {
                "t": "note", "k": "tip",
                "x": "游戏内请切换到顶部的「物品查询」标签页：支持中文名 / 英文名 / 代码搜索，点击代码即可复制。",
            })

    data = {"title": title, "chapters": chapters}

    out = os.path.abspath(OUT)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)

    print("chapters:", len(chapters))
    for ch in chapters:
        print("  %-4s %-28s blocks=%d" % (ch["id"], ch["title"][:28], len(ch["blocks"])))
    print("bytes:", os.path.getsize(out))


if __name__ == "__main__":
    main()
