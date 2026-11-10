"""Minimal .xlsx writer (standard library only): several sheets, a bold header row frozen at the
top with an AutoFilter, column widths, a fill colour per row and optional per-cell colours."""
import zipfile
from xml.sax.saxutils import escape

HEADER = 'FFEDEDED'
# Row fills by name (ARGB).
FILLS = {'purple': 'FFE4D7F5', 'pink': 'FFFCE4EC', 'yellow': 'FFFFF6CC', 'blue': 'FFDDEFFB'}
# Fonts: 0 normal, 1 bold (header), 2 bold white, 3 bold black.
FONTS = ['<font><sz val="10"/><name val="맑은 고딕"/></font>',
         '<font><b/><sz val="10"/><name val="맑은 고딕"/></font>',
         '<font><b/><sz val="10"/><color rgb="FFFFFFFF"/><name val="맑은 고딕"/></font>',
         '<font><b/><sz val="10"/><color rgb="FF000000"/><name val="맑은 고딕"/></font>']


def _col(n):
    s = ''
    n += 1
    while n:
        n, r = divmod(n - 1, 26)
        s = chr(65 + r) + s
    return s


def _cell(ref, value, style):
    st = f' s="{style}"' if style else ''
    if value is None or value == '':
        return f'<c r="{ref}"{st}/>'
    if isinstance(value, bool):
        value = int(value)
    if isinstance(value, (int, float)):
        return f'<c r="{ref}"{st}><v>{value}</v></c>'
    return f'<c r="{ref}"{st} t="inlineStr"><is><t xml:space="preserve">{escape(str(value))}</t></is></c>'


class _Styles:
    """Cell formats keyed by (font index, fill ARGB or None)."""

    def __init__(self):
        self.fills = [None, 'gray125', HEADER]   # fill ids 0..2 are fixed
        self.xfs = [(0, 0), (1, 2)]              # 0 default, 1 header
        self.index = {(0, None): 0}

    def get(self, font, fill_rgb):
        key = (font, fill_rgb)
        if key not in self.index:
            if fill_rgb is None:
                fill_id = 0
            else:
                if fill_rgb not in self.fills:
                    self.fills.append(fill_rgb)
                fill_id = self.fills.index(fill_rgb)
            self.xfs.append((font, fill_id))
            self.index[key] = len(self.xfs) - 1
        return self.index[key]

    def xml(self):
        fills = ['<fill><patternFill patternType="none"/></fill>', '<fill><patternFill patternType="gray125"/></fill>']
        fills += [f'<fill><patternFill patternType="solid"><fgColor rgb="{rgb}"/><bgColor indexed="64"/></patternFill></fill>' for rgb in self.fills[2:]]
        xfs = ''.join(f'<xf numFmtId="0" fontId="{font}" fillId="{fill}" borderId="0" xfId="0"'
                      + (' applyFont="1"' if font else '') + (' applyFill="1"' if fill else '') + '/>' for font, fill in self.xfs)
        return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
                f'<fonts count="{len(FONTS)}">{"".join(FONTS)}</fonts>'
                f'<fills count="{len(fills)}">{"".join(fills)}</fills>'
                '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>'
                '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>'
                f'<cellXfs count="{len(self.xfs)}">{xfs}</cellXfs>'
                '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>'
                '</styleSheet>')


def write(path, sheets):
    """sheets: list of (name, headers, rows, row_fills, widths[, cell_colours]).
    row_fills: a FILLS key or None per row (True means purple).
    cell_colours: per row a dict {column index: (fill RGB like 'FF074CA1', white_text bool)}."""
    styles = _Styles()
    sheet_parts = []
    for i, sheet in enumerate(sheets):
        name, headers, rows, flags, widths = sheet[:5]
        cell_colours = sheet[5] if len(sheet) > 5 else [None] * len(rows)
        last = _col(len(headers) - 1)
        cols = ''.join(f'<col min="{j + 1}" max="{j + 1}" width="{w}" customWidth="1"/>' for j, w in enumerate(widths))
        out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
               '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
               'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
               f'<dimension ref="A1:{last}{len(rows) + 1}"/>'
               '<sheetViews><sheetView workbookViewId="0"' + (' tabSelected="1"' if i == 0 else '') + '>'
               '<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>'
               '<sheetFormatPr defaultRowHeight="15"/>'
               f'<cols>{cols}</cols><sheetData>']
        out.append('<row r="1">' + ''.join(_cell(f'{_col(j)}1', h, 1) for j, h in enumerate(headers)) + '</row>')
        for r, (row, fill, special) in enumerate(zip(rows, flags, cell_colours), start=2):
            fill = 'purple' if fill is True else fill
            base = styles.get(0, FILLS[fill]) if fill else 0
            cells = []
            for j, v in enumerate(row):
                st = base
                if special and j in special:
                    rgb, white = special[j]
                    st = styles.get(2 if white else 3, rgb)
                cells.append(_cell(f'{_col(j)}{r}', v, st))
            out.append(f'<row r="{r}">' + ''.join(cells) + '</row>')
        out.append(f'</sheetData><autoFilter ref="A1:{last}{len(rows) + 1}"/></worksheet>')
        sheet_parts.append(''.join(out))

    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        overrides = ''.join(f'<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
                            for i in range(len(sheets)))
        z.writestr('[Content_Types].xml',
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                   '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                   '<Default Extension="xml" ContentType="application/xml"/>'
                   '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
                   '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
                   + overrides + '</Types>')
        z.writestr('_rels/.rels',
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                   '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
                   '</Relationships>')
        sheet_xml = ''.join(f'<sheet name="{escape(s[0])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>' for i, s in enumerate(sheets))
        names = ''.join(f'<definedName name="_xlnm._FilterDatabase" localSheetId="{i}" hidden="1">\'{s[0]}\'!$A$1:${_col(len(s[1]) - 1)}${len(s[2]) + 1}</definedName>'
                        for i, s in enumerate(sheets))
        z.writestr('xl/workbook.xml',
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
                   'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
                   f'<sheets>{sheet_xml}</sheets><definedNames>{names}</definedNames></workbook>')
        rels = ''.join(f'<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>'
                       for i in range(len(sheets)))
        z.writestr('xl/_rels/workbook.xml.rels',
                   '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                   '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                   + rels + f'<Relationship Id="rId{len(sheets) + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>'
                   '</Relationships>')
        z.writestr('xl/styles.xml', styles.xml())
        for i, part in enumerate(sheet_parts):
            z.writestr(f'xl/worksheets/sheet{i + 1}.xml', part)
