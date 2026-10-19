# Writes Content-addonmanager/panorama/layout/custom_game/pmm_mapvote.xml.
# Row counts must match the plugin: VoteRows = Plugin.RowPool, NomRows = Plugin.NomPool,
# PageSize = Plugin.NomPageSize, SetRows = Plugin.SetRows.
#   python tools/gen_layout.py
import os

VOTE_ROWS = 10
NOM_ROWS = 60
PAGE_SIZE = 6
SET_ROWS = 5
NOM_PAGES = (NOM_ROWS + PAGE_SIZE - 1) // PAGE_SIZE

HERE = os.path.dirname(os.path.abspath(__file__))
# Working folder: ../Content-addonmanager/panorama, GitHub repo: ../PMM_GG1MapChooser/panorama
PANORAMA = next((p for p in (os.path.join(HERE, "..", "Content-addonmanager", "panorama"),
                             os.path.join(HERE, "..", "PMM_GG1MapChooser", "panorama")) if os.path.isdir(p)),
                os.path.join(HERE, "..", "Content-addonmanager", "panorama"))
OUT = os.path.join(PANORAMA, "layout", "custom_game", "pmm_mapvote.xml")

L = []


def w(depth, s):
    L.append("\t" * depth + s)


def gear(depth, prefix, keys):
    # Mode chip: current vote mode + the key that opens the settings (per mode class on the root).
    w(depth, f'<Button id="{prefix}-gear" class="mv-gear">')
    w(depth + 1, '<Panel class="mv-gear-ic" hittest="false" />')
    for mode in ("click", "wasd", "digits", "chat"):
        w(depth + 1, f'<Label class="mv-gear-name gm-{mode} stratum-bold-tf" text="{{s:md_{mode}}}" />')
    for mode, key in keys.items():
        w(depth + 1, f'<Label class="mv-gear-key gk-{mode} stratum-bold-tf" text="{key}" />')
    w(depth, '</Button>')


def hints(depth, prefix, modes):
    for mode in modes:
        w(depth, f'<Label class="mv-hint mv-hint-{mode} stratum-medium-tf" text="{{s:{prefix}_hint_{mode}}}" />')


L.append("<root>")
w(1, "<styles>")
w(2, '<include src="s2r://panorama/styles/csgostyles.vcss_c" />')
w(2, '<include src="s2r://panorama/styles/custom_game/pmm_mapvote.vcss_c" />')
w(2, '<include src="s2r://panorama/styles/custom_game/pmm_mapvote_maps.vcss_c" />')
w(1, "</styles>")
w(1, '<Panel class="mv-screen" hittest="false">')

# ------------------------------------------------------------------ vote
w(2, '<Panel id="mv-root" class="mv-root mv-hidden pos-right" hittest="false">')
w(3, '<Panel class="mv-vote">')
w(4, '<Panel class="mv-head">')
w(5, '<Panel class="mv-headtext" hittest="false">')
w(6, '<Label class="mv-brand stratum-bold-tf" text="{s:mv_brand}" />')
w(6, '<Label class="mv-title stratum-bold-tf" text="{s:mv_title}" />')
w(5, "</Panel>")
gear(5, "mv", {"wasd": "R", "digits": "0", "chat": "!vm"})
w(5, '<Panel class="mv-timer" hittest="false">')
w(6, '<Panel class="mv-timer-ic" />')
w(6, '<Label class="mv-time stratum-bold-tf" text="{s:mv_time}" />')
w(5, "</Panel>")
w(5, '<Button id="mv-close" class="mv-x"><Label class="stratum-bold-tf" text="×" /></Button>')
w(4, "</Panel>")
w(4, '<Panel class="mv-tbar" hittest="false"><Panel id="mv-tfill" class="mv-tfill t20" hittest="false" /></Panel>')
w(4, '<Panel class="mv-rows">')
for i in range(VOTE_ROWS):
    w(4, f'<Button id="mv-row-{i}" class="mv-row mv-off">')
    w(5, f'<Panel class="mv-num"><Label class="stratum-bold-tf" text="{i + 1}" /></Panel>')
    w(5, f'<Panel id="mv-art-{i}" class="mv-art" hittest="false" />')
    w(5, '<Panel class="mv-info" hittest="false">')
    w(6, '<Panel class="mv-line" hittest="false">')
    w(7, f'<Label class="mv-name mv-mapname stratum-bold-tf" text="{{s:name{i}}}" />')
    w(7, f'<Label class="mv-name mv-sname stratum-bold-tf" text="{{s:sname{i}}}" />')
    w(7, f'<Label id="mv-badge-{i}" class="mv-badge stratum-bold-tf" text="{{s:badge{i}}}" />')
    w(7, '<Label class="mv-mine stratum-bold-tf" text="{s:mv_yours}" />')
    w(6, "</Panel>")
    w(6, f'<Panel class="mv-bar" hittest="false"><Panel id="mv-fill-{i}" class="mv-fill v0" hittest="false" /></Panel>')
    w(5, "</Panel>")
    w(5, f'<Label class="mv-cnt stratum-bold-tf" text="{{s:cnt{i}}}" />')
    w(5, '<Panel class="mv-tick" hittest="false" />')
    w(4, "</Button>")
w(4, "</Panel>")
w(4, '<Panel class="mv-foot" hittest="false">')
hints(5, "mv", ("click", "chat", "wasd", "digits"))
w(5, '<Label class="mv-voted stratum-medium-tf" text="{s:mv_voted}" />')
w(4, "</Panel>")
w(3, "</Panel>")
w(3, '<Panel id="mv-card" class="mv-card" hittest="false">')
w(4, '<Label class="mv-brand stratum-bold-tf" text="{s:mv_brand}" />')
w(4, '<Label class="mv-card-sub stratum-bold-tf" text="{s:card_sub}" />')
w(4, '<Panel id="mv-card-art" class="mv-card-art">')
w(5, '<Panel class="mv-card-check" />')
w(4, "</Panel>")
w(4, '<Panel class="mv-card-line">')
w(5, '<Label class="mv-card-title mv-card-maptitle stratum-bold-tf" text="{s:card_title}" />')
w(5, '<Label class="mv-card-title mv-card-stitle stratum-bold-tf" text="{s:card_stitle}" />')
w(5, '<Label id="mv-card-badge" class="mv-badge mv-card-badge stratum-bold-tf" text="{s:card_badge}" />')
w(4, "</Panel>")
w(3, "</Panel>")
w(2, "</Panel>")

# ------------------------------------------------------------------ nominate
w(2, '<Panel id="nm-root" class="mv-root nm-root mv-hidden pos-right" hittest="false">')
w(3, '<Panel class="mv-vote">')
w(4, '<Panel class="mv-head">')
w(5, '<Panel class="mv-headtext" hittest="false">')
w(6, '<Label class="mv-brand stratum-bold-tf" text="{s:nm_brand}" />')
w(6, '<Label class="mv-title stratum-bold-tf" text="{s:nm_title}" />')
w(5, "</Panel>")
gear(5, "nm", {"wasd": "!vm", "digits": "!vm", "chat": "!vm"})
w(5, '<Button id="nm-close" class="mv-x"><Label class="stratum-bold-tf" text="×" /></Button>')
w(4, "</Panel>")
w(4, '<Panel class="mv-tbar" hittest="false" />')
w(4, '<Panel class="mv-rows">')
for i in range(NOM_ROWS):
    w(4, f'<Button id="nm-row-{i}" class="mv-row nm-row mv-off">')
    w(5, f'<Panel class="mv-num"><Label class="stratum-bold-tf" text="{i % PAGE_SIZE + 1}" /></Panel>')
    w(5, f'<Panel id="nm-art-{i}" class="mv-art" hittest="false" />')
    w(5, '<Panel class="mv-info" hittest="false">')
    w(6, '<Panel class="mv-line" hittest="false">')
    w(7, f'<Label class="mv-name stratum-bold-tf" text="{{s:nmname{i}}}" />')
    w(7, f'<Label id="nm-badge-{i}" class="mv-badge stratum-bold-tf" text="{{s:nmbadge{i}}}" />')
    w(6, "</Panel>")
    w(6, '<Panel class="nm-states" hittest="false">')
    w(7, '<Label class="nm-st nm-st-taken stratum-bold-tf" text="{s:nm_taken}" />')
    w(7, '<Label class="nm-st nm-st-mine stratum-bold-tf" text="{s:nm_mine}" />')
    w(7, '<Label class="nm-st nm-st-cool stratum-bold-tf" text="{s:nm_cool}" />')
    w(6, "</Panel>")
    w(5, "</Panel>")
    w(4, "</Button>")
w(4, "</Panel>")
w(4, '<Panel class="nm-pager">')
w(5, '<Button id="nm-prev" class="nm-pbtn"><Label class="stratum-bold-tf" text="‹" /></Button>')
w(5, '<Panel class="nm-pages" hittest="false">')
for p in range(NOM_PAGES):
    w(6, f'<Label id="nm-pg-{p}" class="nm-pg stratum-bold-tf" text="{p + 1} / {{s:nm_pages}}" />')
w(5, "</Panel>")
w(5, '<Button id="nm-next" class="nm-pbtn"><Label class="stratum-bold-tf" text="›" /></Button>')
w(5, '<Label class="nm-count stratum-medium-tf" text="{s:nm_count}" />')
w(4, "</Panel>")
w(4, '<Panel class="mv-foot" hittest="false">')
hints(5, "nm", ("click", "wasd", "digits", "chat"))
w(4, "</Panel>")
w(3, "</Panel>")
w(2, "</Panel>")

# ------------------------------------------------------------------ settings
w(2, '<Panel id="st-root" class="mv-root st-root mv-hidden pos-right" hittest="false">')
w(3, '<Panel class="mv-vote">')
w(4, '<Panel class="mv-head">')
w(5, '<Panel class="mv-headtext" hittest="false">')
w(6, '<Label class="mv-brand stratum-bold-tf" text="{s:st_brand}" />')
w(6, '<Label class="mv-title stratum-bold-tf" text="{s:st_title}" />')
w(5, "</Panel>")
w(5, '<Button id="st-close" class="mv-x"><Label class="stratum-bold-tf" text="×" /></Button>')
w(4, "</Panel>")
w(4, '<Label class="st-sub stratum-medium-tf" text="{s:st_sub}" />')
w(4, '<Panel class="mv-tbar" hittest="false" />')
w(4, '<Panel class="mv-rows">')
for i in range(SET_ROWS):
    w(4, f'<Button id="st-row-{i}" class="mv-row st-row st-k{i}">')
    w(5, f'<Panel class="mv-num"><Label class="stratum-bold-tf" text="{i + 1}" /></Panel>')
    w(5, '<Panel class="st-ic" hittest="false" />')
    w(5, '<Panel class="mv-info" hittest="false">')
    w(6, f'<Label class="mv-name stratum-bold-tf" text="{{s:st_name{i}}}" />')
    w(6, f'<Label class="st-desc stratum-medium-tf" text="{{s:st_desc{i}}}" />')
    w(5, "</Panel>")
    w(5, '<Panel class="mv-tick" hittest="false" />')
    w(4, "</Button>")
w(4, "</Panel>")
w(4, '<Panel class="mv-foot" hittest="false">')
hints(5, "st", ("click", "wasd", "digits", "chat"))
w(4, "</Panel>")
w(3, "</Panel>")
w(2, "</Panel>")

w(1, "</Panel>")
L.append("</root>")

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8", newline="\r\n") as f:
    f.write("\n".join(L) + "\n")
print("written", os.path.normpath(OUT), len(L), "lines")
