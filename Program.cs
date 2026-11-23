using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("SafeSweep — Safe Windows Disk Cleaner")]
[assembly: AssemblyDescription("Transparent, safety-first Windows disk cleaner showing folder-by-folder breakdowns before cleaning.")]
[assembly: AssemblyProduct("SafeSweep")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace SafeSweep
{
    internal sealed class LaunchArgs
    {
        public bool DemoMode;
        public string ForceLang;
        public bool ShowInspector;
        public bool ShowConfirm;

        public static LaunchArgs Parse(string[] args)
        {
            LaunchArgs opt = new LaunchArgs();
            if (args == null)
                return opt;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").Trim().ToLowerInvariant();
                if (a == "--demo") opt.DemoMode = true;
                else if (a == "--show-inspector") opt.ShowInspector = true;
                else if (a == "--show-confirm") opt.ShowConfirm = true;
                else if (a == "--lang" && i + 1 < args.Length)
                {
                    string l = (args[++i] ?? "").Trim().ToLowerInvariant();
                    if (l == "tr" || l == "en")
                        opt.ForceLang = l;
                }
            }
            return opt;
        }
    }

    internal sealed class AppPrefs
    {
        public string Language; // "en" or "tr"

        public AppPrefs()
        {
            bool isTr = false;
            try
            {
                isTr = string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
            }
            Language = isTr ? "tr" : "en";
        }

        public bool IsTurkish
        {
            get { return string.Equals(Language, "tr", StringComparison.OrdinalIgnoreCase); }
        }

        private static string PrefPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(Path.Combine(appData, "SafeSweep"), "settings.ini");
        }

        public static AppPrefs Load()
        {
            AppPrefs p = new AppPrefs();
            try
            {
                string path = PrefPath();
                if (!File.Exists(path)) return p;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "Language" && (v == "tr" || v == "en"))
                        p.Language = v;
                }
            }
            catch
            {
            }
            return p;
        }

        public void Save()
        {
            try
            {
                string path = PrefPath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, "Language=" + (Language ?? "en") + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    internal sealed class CleanupListPanel : Control
    {
        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColRow = Color.FromArgb(22, 27, 34);
        private static readonly Color ColRowHover = Color.FromArgb(28, 35, 45);
        private static readonly Color ColRowBorder = Color.FromArgb(40, 49, 62);
        private static readonly Color ColRowBorderHover = Color.FromArgb(62, 75, 94);
        private static readonly Color ColTextPrimary = Color.FromArgb(240, 246, 252);
        private static readonly Color ColTextSecondary = Color.FromArgb(139, 148, 158);
        private static readonly Color ColTextMuted = Color.FromArgb(110, 118, 129);
        private static readonly Color ColSafe = Color.FromArgb(52, 211, 153);
        private static readonly Color ColSafeBg = Color.FromArgb(18, 46, 36);
        private static readonly Color ColSafeBorder = Color.FromArgb(36, 122, 92);
        private static readonly Color ColCaution = Color.FromArgb(251, 191, 36);
        private static readonly Color ColCautionBg = Color.FromArgb(48, 38, 18);
        private static readonly Color ColCautionBorder = Color.FromArgb(150, 102, 22);
        private static readonly Color ColWarnBg = Color.FromArgb(52, 32, 22);
        private static readonly Color ColWarnText = Color.FromArgb(251, 146, 60);
        private static readonly Color ColBtnBg = Color.FromArgb(30, 37, 48);
        private static readonly Color ColBtnHover = Color.FromArgb(44, 54, 68);
        private static readonly Color ColBtnBorder = Color.FromArgb(58, 70, 88);
        private static readonly Color ColScrollTrack = Color.FromArgb(18, 23, 30);
        private static readonly Color ColScrollThumb = Color.FromArgb(54, 65, 82);
        private static readonly Color ColScrollThumbHover = Color.FromArgb(80, 96, 118);

        private List<CleanupCategory> _items = new List<CleanupCategory>();
        private float _scale = 1.0f;
        private bool _isTurkish;
        private bool _isBusy;
        private long _maxCategoryBytes = 1;
        private int _scrollY;
        private int _hoverRow = -1;
        private bool _hoverInspect;
        private bool _hoverScroll;
        private bool _draggingScroll;
        private int _dragStartY;
        private int _dragStartScroll;

        private Font _fontTitle;
        private Font _fontBadge;
        private Font _fontPath;
        private Font _fontSize;
        private Font _fontCount;
        private Font _fontBtn;

        public event EventHandler SelectionChanged;
        public event Action<CleanupCategory> InspectRequested;

        public CleanupListPanel()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);
            BackColor = ColBg;
            RebuildFonts();
        }

        public void SetScale(float scale)
        {
            _scale = scale;
            RebuildFonts();
            ClampScroll();
            Invalidate();
        }

        public void SetLanguage(bool isTurkish)
        {
            _isTurkish = isTurkish;
            Invalidate();
        }

        public void SetBusy(bool busy)
        {
            _isBusy = busy;
            Invalidate();
        }

        public void SetItems(List<CleanupCategory> visibleItems)
        {
            _items = visibleItems ?? new List<CleanupCategory>();
            _maxCategoryBytes = 1;
            foreach (CleanupCategory c in _items)
            {
                if (c.TotalBytes > _maxCategoryBytes)
                    _maxCategoryBytes = c.TotalBytes;
            }
            ClampScroll();
            Invalidate();
        }

        private float Sf(float v) { return v * _scale; }
        private float PtToPx(float pt) { return pt * (96f / 72f) * _scale; }
        private int RowHeightPx() { return (int)Math.Round(66f * _scale); }
        private int RowGapPx() { return (int)Math.Round(8f * _scale); }
        private int ScrollbarWidthPx() { return (int)Math.Round(10f * _scale); }

        private void RebuildFonts()
        {
            DisposeFonts();
            _fontTitle = new Font("Segoe UI", PtToPx(9.6f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontBadge = new Font("Segoe UI", PtToPx(7.0f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontPath = new Font("Segoe UI", PtToPx(8.0f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSize = new Font("Segoe UI", PtToPx(10.2f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCount = new Font("Segoe UI", PtToPx(7.8f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontBtn = new Font("Segoe UI", PtToPx(8.0f), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            if (_fontTitle != null) { _fontTitle.Dispose(); _fontTitle = null; }
            if (_fontBadge != null) { _fontBadge.Dispose(); _fontBadge = null; }
            if (_fontPath != null) { _fontPath.Dispose(); _fontPath = null; }
            if (_fontSize != null) { _fontSize.Dispose(); _fontSize = null; }
            if (_fontCount != null) { _fontCount.Dispose(); _fontCount = null; }
            if (_fontBtn != null) { _fontBtn.Dispose(); _fontBtn = null; }
        }

        private int TotalContentHeight()
        {
            if (_items.Count == 0) return 0;
            return _items.Count * (RowHeightPx() + RowGapPx()) - RowGapPx() + (int)Math.Round(4f * _scale);
        }

        private int MaxScroll()
        {
            return Math.Max(0, TotalContentHeight() - ClientSize.Height);
        }

        private void ClampScroll()
        {
            int max = MaxScroll();
            if (_scrollY > max) _scrollY = max;
            if (_scrollY < 0) _scrollY = 0;
        }

        private bool NeedsScrollbar()
        {
            return MaxScroll() > 0;
        }

        private RectangleF GetRowRect(int index)
        {
            float rightMargin = NeedsScrollbar() ? ScrollbarWidthPx() + Sf(6f) : 0f;
            float y = index * (RowHeightPx() + RowGapPx()) - _scrollY;
            return new RectangleF(0f, y, Math.Max(100f, ClientSize.Width - rightMargin - 1f), RowHeightPx());
        }

        private RectangleF GetInspectBtnRect(RectangleF rowRect)
        {
            float bw = Sf(68f);
            float bh = Sf(28f);
            return new RectangleF(rowRect.Right - bw - Sf(12f), rowRect.Y + (rowRect.Height - bh) * 0.5f, bw, bh);
        }

        private RectangleF GetScrollThumbRect()
        {
            int total = TotalContentHeight();
            if (total <= ClientSize.Height || ClientSize.Height <= 0)
                return RectangleF.Empty;

            float trackX = ClientSize.Width - ScrollbarWidthPx();
            float trackH = ClientSize.Height;
            float thumbH = Math.Max(Sf(32f), trackH * ((float)ClientSize.Height / total));
            float ratio = (float)_scrollY / Math.Max(1, MaxScroll());
            float thumbY = ratio * (trackH - thumbH);
            return new RectangleF(trackX + Sf(2f), thumbY, ScrollbarWidthPx() - Sf(4f), thumbH);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!NeedsScrollbar()) return;
            int step = (int)Math.Round(52f * _scale);
            _scrollY -= (e.Delta / 120) * step;
            ClampScroll();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_draggingScroll)
            {
                RectangleF thumb = GetScrollThumbRect();
                float availTrack = Math.Max(1f, ClientSize.Height - thumb.Height);
                int dy = e.Y - _dragStartY;
                _scrollY = _dragStartScroll + (int)Math.Round((dy / availTrack) * MaxScroll());
                ClampScroll();
                Invalidate();
                return;
            }

            RectangleF thumbRect = GetScrollThumbRect();
            bool onScroll = !thumbRect.IsEmpty && e.X >= ClientSize.Width - ScrollbarWidthPx() - Sf(2f);

            int hitRow = -1;
            bool hitInspect = false;
            if (!onScroll)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    RectangleF rr = GetRowRect(i);
                    if (rr.Bottom < 0 || rr.Top > ClientSize.Height) continue;
                    if (rr.Contains(e.Location))
                    {
                        hitRow = i;
                        hitInspect = GetInspectBtnRect(rr).Contains(e.Location);
                        break;
                    }
                }
            }

            if (hitRow != _hoverRow || hitInspect != _hoverInspect || onScroll != _hoverScroll)
            {
                _hoverRow = hitRow;
                _hoverInspect = hitInspect;
                _hoverScroll = onScroll;
                Cursor = (hitRow >= 0 || onScroll) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverRow != -1 || _hoverInspect || _hoverScroll)
            {
                _hoverRow = -1;
                _hoverInspect = false;
                _hoverScroll = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;

            RectangleF thumbRect = GetScrollThumbRect();
            if (!thumbRect.IsEmpty && e.X >= ClientSize.Width - ScrollbarWidthPx() - Sf(2f))
            {
                _draggingScroll = true;
                _dragStartY = e.Y;
                _dragStartScroll = _scrollY;
                Capture = true;
                return;
            }

            if (_isBusy) return;

            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF rr = GetRowRect(i);
                if (rr.Bottom < 0 || rr.Top > ClientSize.Height) continue;
                if (rr.Contains(e.Location))
                {
                    if (GetInspectBtnRect(rr).Contains(e.Location))
                    {
                        if (InspectRequested != null)
                            InspectRequested(_items[i]);
                    }
                    else
                    {
                        _items[i].Checked = !_items[i].Checked;
                        Invalidate();
                        if (SelectionChanged != null)
                            SelectionChanged(this, EventArgs.Empty);
                    }
                    break;
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingScroll)
            {
                _draggingScroll = false;
                Capture = false;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_isBusy || e.Button != MouseButtons.Left) return;

            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF rr = GetRowRect(i);
                if (rr.Contains(e.Location) && !GetInspectBtnRect(rr).Contains(e.Location))
                {
                    // Revert the second toggle from the double click and open inspector
                    _items[i].Checked = !_items[i].Checked;
                    Invalidate();
                    if (SelectionChanged != null)
                        SelectionChanged(this, EventArgs.Empty);
                    if (InspectRequested != null)
                        InspectRequested(_items[i]);
                    break;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(ColBg);

            if (_items.Count == 0)
            {
                using (SolidBrush b = new SolidBrush(ColTextMuted))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    string msg = _isBusy
                        ? (_isTurkish ? "Klasörler güvenli şekilde taranıyor..." : "Scanning system folders safely...")
                        : (_isTurkish ? "Temizlenecek öğe bulunamadı — bilgisayarınız temiz görünüyor." : "Nothing to clean — your system looks spotless.");
                    g.DrawString(msg, _fontTitle, b, ClientRectangle, sf);
                }
                return;
            }

            CultureInfo c = CleanupEngine.GetCulture(_isTurkish);

            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF rr = GetRowRect(i);
                if (rr.Bottom < -4 || rr.Top > ClientSize.Height + 4)
                    continue;

                DrawRow(g, _items[i], rr, i == _hoverRow, i == _hoverRow && _hoverInspect, c);
            }

            if (NeedsScrollbar())
            {
                float trackX = ClientSize.Width - ScrollbarWidthPx();
                using (SolidBrush tb = new SolidBrush(ColScrollTrack))
                    g.FillRectangle(tb, trackX, 0, ScrollbarWidthPx(), ClientSize.Height);

                RectangleF thumb = GetScrollThumbRect();
                using (GraphicsPath tp = CreateRoundedRect(thumb, Sf(3f)))
                using (SolidBrush sb = new SolidBrush((_hoverScroll || _draggingScroll) ? ColScrollThumbHover : ColScrollThumb))
                    g.FillPath(sb, tp);
            }
        }

        private void DrawRow(Graphics g, CleanupCategory cat, RectangleF rr, bool hovered, bool inspectHovered, CultureInfo c)
        {
            Color bg = hovered ? ColRowHover : ColRow;
            Color border = hovered ? ColRowBorderHover : ColRowBorder;
            DrawRoundedBox(g, rr, Sf(10f), bg, border);

            // 1. Checkbox
            float cbSize = Sf(18f);
            RectangleF cbRect = new RectangleF(rr.X + Sf(14f), rr.Y + (rr.Height - cbSize) * 0.5f, cbSize, cbSize);
            if (cat.Checked)
            {
                Color checkBg = cat.Tier == SafetyTier.Safe ? ColSafe : ColCaution;
                DrawRoundedBox(g, cbRect, Sf(5f), checkBg, checkBg);
                using (Pen cp = new Pen(ColBg, Math.Max(1.8f, Sf(2.0f))))
                {
                    cp.StartCap = LineCap.Round;
                    cp.EndCap = LineCap.Round;
                    cp.LineJoin = LineJoin.Round;
                    PointF[] pts = new PointF[]
                    {
                        new PointF(cbRect.X + cbRect.Width * 0.22f, cbRect.Y + cbRect.Height * 0.52f),
                        new PointF(cbRect.X + cbRect.Width * 0.43f, cbRect.Y + cbRect.Height * 0.74f),
                        new PointF(cbRect.X + cbRect.Width * 0.79f, cbRect.Y + cbRect.Height * 0.29f)
                    };
                    g.DrawLines(cp, pts);
                }
            }
            else
            {
                DrawRoundedBox(g, cbRect, Sf(5f), ColBg, Color.FromArgb(72, 84, 102));
            }

            // Right side geometry
            RectangleF inspectRect = GetInspectBtnRect(rr);
            float statsW = Sf(132f);
            float statsX = inspectRect.X - statsW - Sf(12f);

            // 2. Title + Badges
            float textX = rr.X + Sf(44f);
            float maxTextW = Math.Max(Sf(140f), statsX - textX - Sf(10f));
            string title = cat.GetName(_isTurkish);

            float badgeStartX;
            using (StringFormat sfTitle = new StringFormat(StringFormat.GenericTypographic))
            {
                sfTitle.Alignment = StringAlignment.Near;
                sfTitle.LineAlignment = StringAlignment.Center;
                sfTitle.FormatFlags |= StringFormatFlags.NoWrap;
                sfTitle.Trimming = StringTrimming.EllipsisCharacter;

                SizeF tSize = g.MeasureString(title, _fontTitle, PointF.Empty, sfTitle);
                float drawW = Math.Min(tSize.Width + Sf(4f), maxTextW - Sf(76f));
                RectangleF titleRect = new RectangleF(textX, rr.Y + Sf(10f), drawW, Sf(20f));
                using (SolidBrush tb = new SolidBrush(cat.TotalBytes > 0 ? ColTextPrimary : ColTextMuted))
                {
                    g.DrawString(title, _fontTitle, tb, titleRect, sfTitle);
                }
                badgeStartX = textX + drawW + Sf(8f);
            }

            // Safety tier badge
            string tierLabel = cat.Tier == SafetyTier.Safe
                ? (_isTurkish ? "GÜVENLİ" : "SAFE")
                : (_isTurkish ? "DİKKAT" : "CAUTION");
            Color tierBg = cat.Tier == SafetyTier.Safe ? ColSafeBg : ColCautionBg;
            Color tierBd = cat.Tier == SafetyTier.Safe ? ColSafeBorder : ColCautionBorder;
            Color tierFg = cat.Tier == SafetyTier.Safe ? ColSafe : ColCaution;

            badgeStartX = DrawPillTag(g, badgeStartX, rr.Y + Sf(12f), tierLabel, tierBg, tierBd, tierFg, statsX - Sf(8f));

            // Optional running app badge
            if (cat.IsAppRunning && badgeStartX < statsX - Sf(54f))
            {
                string runLbl = _isTurkish ? "AÇIK" : "RUNNING";
                badgeStartX = DrawPillTag(g, badgeStartX, rr.Y + Sf(12f), runLbl, ColWarnBg, Color.FromArgb(148, 78, 36), ColWarnText, statsX - Sf(8f));
            }

            // Optional partial admin badge
            if (cat.AccessDeniedFolders > 0 && !CleanupEngine.IsAdministrator() && badgeStartX < statsX - Sf(64f))
            {
                string admLbl = _isTurkish ? "KISMİ" : "PARTIAL";
                DrawPillTag(g, badgeStartX, rr.Y + Sf(12f), admLbl, Color.FromArgb(28, 35, 46), Color.FromArgb(62, 74, 92), ColTextSecondary, statsX - Sf(8f));
            }

            // 3. Folder Path + Short description
            string subLine = cat.DisplayPath + "  ·  " + cat.GetDescription(_isTurkish);
            RectangleF pathRect = new RectangleF(textX, rr.Y + Sf(34f), maxTextW, Sf(20f));
            using (SolidBrush pb = new SolidBrush(ColTextSecondary))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(subLine, _fontPath, pb, pathRect, sf);
            }

            // 4. Right Stats (Size + File Count + Proportional mini bar)
            string sizeStr = CleanupEngine.FormatBytes(cat.TotalBytes, _isTurkish);
            Color sizeCol = cat.TotalBytes == 0
                ? ColTextMuted
                : (cat.Tier == SafetyTier.Caution ? ColCaution : (cat.TotalBytes >= 100L * 1024 * 1024 ? ColSafe : ColTextPrimary));

            RectangleF sizeRect = new RectangleF(statsX, rr.Y + Sf(9f), statsW, Sf(21f));
            using (SolidBrush sb = new SolidBrush(sizeCol))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(sizeStr, _fontSize, sb, sizeRect, sf);
            }

            string countStr = string.Format(c, "{0:N0} {1}", cat.FileCount, _isTurkish ? "dosya" : (cat.FileCount == 1 ? "file" : "files"));
            RectangleF cntRect = new RectangleF(statsX, rr.Y + Sf(31f), statsW, Sf(16f));
            using (SolidBrush cb = new SolidBrush(ColTextMuted))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(countStr, _fontCount, cb, cntRect, sf);
            }

            // Proportional share bar
            float barW = Sf(96f);
            float barH = Sf(3.5f);
            float barX = statsX + statsW - barW;
            float barY = rr.Y + Sf(51f);
            using (GraphicsPath trackP = CreateRoundedRect(new RectangleF(barX, barY, barW, barH), Sf(1.7f)))
            using (SolidBrush trackB = new SolidBrush(Color.FromArgb(34, 42, 54)))
                g.FillPath(trackB, trackP);

            if (cat.TotalBytes > 0)
            {
                float ratio = (float)Math.Min(1.0, Math.Max(0.05, (double)cat.TotalBytes / Math.Max(1L, _maxCategoryBytes)));
                float fillW = barW * ratio;
                Color fillCol = cat.Tier == SafetyTier.Safe ? ColSafe : ColCaution;
                using (GraphicsPath fillP = CreateRoundedRect(new RectangleF(barX + (barW - fillW), barY, fillW, barH), Sf(1.7f)))
                using (SolidBrush fillB = new SolidBrush(fillCol))
                    g.FillPath(fillB, fillP);
            }

            // 5. Inspect button
            DrawRoundedBox(g, inspectRect, Sf(7f), inspectHovered ? ColBtnHover : ColBtnBg, inspectHovered ? ColSafe : ColBtnBorder);
            using (SolidBrush ib = new SolidBrush(inspectHovered ? ColTextPrimary : Color.FromArgb(200, 210, 222)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(_isTurkish ? "İncele" : "Inspect", _fontBtn, ib, inspectRect, sf);
            }
        }

        private float DrawPillTag(Graphics g, float x, float y, string text, Color bg, Color border, Color fg, float maxX)
        {
            SizeF sz;
            using (StringFormat typo = new StringFormat(StringFormat.GenericTypographic))
            {
                sz = g.MeasureString(text, _fontBadge, PointF.Empty, typo);
            }
            float w = sz.Width + Sf(12f);
            float h = Sf(17f);
            if (x + w > maxX)
                return maxX;

            RectangleF r = new RectangleF(x, y, w, h);
            DrawRoundedBox(g, r, Sf(4.5f), bg, border);
            using (SolidBrush fb = new SolidBrush(fg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(text, _fontBadge, fb, r, sf);
            }
            return x + w + Sf(6f);
        }

        private static void DrawRoundedBox(Graphics g, RectangleF r, float radius, Color fill, Color border)
        {
            using (GraphicsPath p = CreateRoundedRect(r, radius))
            using (SolidBrush fb = new SolidBrush(fill))
            using (Pen bp = new Pen(border, 1f))
            {
                g.FillPath(fb, p);
                g.DrawPath(bp, p);
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeFonts();
            base.Dispose(disposing);
        }
    }

    internal sealed class MainForm : Form
    {
        private const int DefaultLogicalW = 790;
        private const int DefaultLogicalH = 640;

        // Interactive chrome hits
        private const int HitNone = -1;
        private const int HitLang = 1;
        private const int HitAdmin = 2;
        private const int HitRescan = 3;
        private const int HitSelSafe = 4;
        private const int HitSelNone = 5;
        private const int HitToggleEmpty = 6;
        private const int HitCopyReport = 7;
        private const int HitClean = 8;

        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColCard = Color.FromArgb(22, 27, 34);
        private static readonly Color ColCardBorder = Color.FromArgb(42, 51, 64);
        private static readonly Color ColDivider = Color.FromArgb(32, 40, 52);
        private static readonly Color ColTextPrimary = Color.FromArgb(240, 246, 252);
        private static readonly Color ColTextBody = Color.FromArgb(204, 212, 220);
        private static readonly Color ColTextSecondary = Color.FromArgb(139, 148, 158);
        private static readonly Color ColTextMuted = Color.FromArgb(110, 118, 129);
        private static readonly Color ColSafe = Color.FromArgb(52, 211, 153);
        private static readonly Color ColCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color ColAmber = Color.FromArgb(251, 191, 36);
        private static readonly Color ColBtnNormal = Color.FromArgb(28, 35, 45);
        private static readonly Color ColBtnHover = Color.FromArgb(40, 49, 63);
        private static readonly Color ColBtnPress = Color.FromArgb(20, 25, 33);
        private static readonly Color ColBtnBorder = Color.FromArgb(52, 63, 80);
        private static readonly Color ColPrimaryBg = Color.FromArgb(16, 124, 88);
        private static readonly Color ColPrimaryHover = Color.FromArgb(20, 148, 105);
        private static readonly Color ColPrimaryDisabled = Color.FromArgb(26, 33, 42);

        private readonly LaunchArgs _launch;
        private readonly AppPrefs _prefs;
        private readonly CleanupListPanel _listPanel;
        private readonly Icon _appIcon;
        private readonly bool _isAdmin;

        private List<CleanupCategory> _allCategories = new List<CleanupCategory>();
        private float _scale = 1.0f;
        private bool _isBusy;
        private bool _cancelFlag;
        private bool _showEmpty;
        private float _progressRatio;
        private string _statusText = "";
        private bool _statusIsSuccess;
        private DateTime _copiedUntil = DateTime.MinValue;
        private System.Windows.Forms.Timer _uiTimer;

        private long _diskFreeBytes;
        private long _diskTotalBytes;
        private string _diskName = "C:";

        // Interactive rects
        private RectangleF _rcLang;
        private RectangleF _rcAdmin;
        private RectangleF _rcRescan;
        private RectangleF _rcSelSafe;
        private RectangleF _rcSelNone;
        private RectangleF _rcToggleEmpty;
        private RectangleF _rcCopyReport;
        private RectangleF _rcClean;

        private int _hoverHit = HitNone;
        private int _pressHit = HitNone;

        private Font _fontBrand;
        private Font _fontTagline;
        private Font _fontSmallPill;
        private Font _fontCardLabel;
        private Font _fontCardBig;
        private Font _fontCardSub;
        private Font _fontToolbar;
        private Font _fontStatus;
        private Font _fontPrimaryBtn;

        public MainForm(LaunchArgs launch)
        {
            _launch = launch ?? new LaunchArgs();
            _prefs = _launch.DemoMode ? new AppPrefs() : AppPrefs.Load();
            if (!string.IsNullOrEmpty(_launch.ForceLang))
                _prefs.Language = _launch.ForceLang;

            _isAdmin = CleanupEngine.IsAdministrator();

            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ColBg;
            DoubleBuffered = true;
            KeyPreview = true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);

            _appIcon = LoadAppIcon();
            if (_appIcon != null)
                Icon = _appIcon;

            _listPanel = new CleanupListPanel();
            _listPanel.SelectionChanged += delegate { Invalidate(); };
            _listPanel.InspectRequested += OnInspectCategory;
            Controls.Add(_listPanel);

            UpdateTitleText();
            UpdateDpiScale(GetInitialDpi());
            ClientSize = new Size(S(DefaultLogicalW), S(DefaultLogicalH));
            MinimumSize = new Size(S(700), S(540));

            RefreshDiskSpace();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 300;
            _uiTimer.Tick += delegate
            {
                if (_copiedUntil != DateTime.MinValue && DateTime.UtcNow >= _copiedUntil)
                {
                    _copiedUntil = DateTime.MinValue;
                    Invalidate();
                }
            };
            _uiTimer.Start();

            if (_launch.DemoMode)
            {
                LoadDemoData();
            }
            else
            {
                StartScanAsync();
            }
        }

        private static Icon LoadAppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        private float GetInitialDpi()
        {
            using (Graphics g = CreateGraphics())
                return g.DpiX > 0 ? g.DpiX : 96f;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeWindowStyle.ApplyDarkTitleBar(Handle, ColBg, ColTextPrimary);

            try
            {
                uint winDpi = NativeWindowStyle.GetDpiForWindow(Handle);
                if (winDpi >= 72)
                {
                    float newScale = winDpi / 96f;
                    if (Math.Abs(newScale - _scale) > 0.01f)
                    {
                        UpdateDpiScale(winDpi);
                        ClientSize = new Size(S(DefaultLogicalW), S(DefaultLogicalH));
                        MinimumSize = new Size(S(700), S(540));
                    }
                }
            }
            catch
            {
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_launch.DemoMode && _launch.ShowInspector && _allCategories.Count > 0)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    OnInspectCategory(_allCategories[0]);
                });
            }
            else if (_launch.DemoMode && _launch.ShowConfirm)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    List<CleanupCategory> sel = GetSelectedCleanable();
                    if (sel.Count > 0)
                    {
                        using (ConfirmCleanDialog dlg = new ConfirmCleanDialog(sel, _prefs.IsTurkish))
                            dlg.ShowDialog(this);
                    }
                });
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeWindowStyle.WM_DPICHANGED)
            {
                int newDpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (newDpi >= 72)
                {
                    UpdateDpiScale(newDpi);
                    Invalidate();
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutElements();
            Invalidate();
        }

        private int S(float logical) { return (int)Math.Round(logical * _scale); }
        private float Sf(float logical) { return logical * _scale; }
        private float PtToPx(float pt) { return pt * (96f / 72f) * _scale; }

        private void UpdateDpiScale(float dpi)
        {
            _scale = dpi / 96f;
            RebuildFonts();
            _listPanel.SetScale(_scale);
            _listPanel.SetLanguage(_prefs.IsTurkish);
            LayoutElements();
        }

        private void RebuildFonts()
        {
            DisposeFonts();
            _fontBrand = new Font("Segoe UI", PtToPx(11f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontTagline = new Font("Segoe UI", PtToPx(8.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSmallPill = new Font("Segoe UI", PtToPx(7.6f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCardLabel = new Font("Segoe UI", PtToPx(7.3f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCardBig = new Font("Segoe UI", PtToPx(18.5f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontCardSub = new Font("Segoe UI", PtToPx(8.1f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontToolbar = new Font("Segoe UI", PtToPx(8.2f), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontStatus = new Font("Segoe UI", PtToPx(8.5f), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPrimaryBtn = new Font("Segoe UI", PtToPx(9.2f), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private void DisposeFonts()
        {
            DisposeObj(ref _fontBrand);
            DisposeObj(ref _fontTagline);
            DisposeObj(ref _fontSmallPill);
            DisposeObj(ref _fontCardLabel);
            DisposeObj(ref _fontCardBig);
            DisposeObj(ref _fontCardSub);
            DisposeObj(ref _fontToolbar);
            DisposeObj(ref _fontStatus);
            DisposeObj(ref _fontPrimaryBtn);
        }

        private static void DisposeObj<T>(ref T obj) where T : class, IDisposable
        {
            if (obj != null) { obj.Dispose(); obj = null; }
        }

        private void LayoutElements()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            float pad = Sf(20f);

            // Top right pills
            _rcLang = new RectangleF(w - pad - Sf(58f), Sf(15f), Sf(58f), Sf(23f));
            _rcAdmin = new RectangleF(_rcLang.X - Sf(88f), Sf(15f), Sf(80f), Sf(23f));

            // Toolbar row (y = 140)
            float tbY = Sf(138f);
            float tbH = Sf(28f);
            _rcRescan = new RectangleF(pad, tbY, Sf(102f), tbH);
            _rcSelSafe = new RectangleF(_rcRescan.Right + Sf(8f), tbY, Sf(114f), tbH);
            _rcSelNone = new RectangleF(_rcSelSafe.Right + Sf(8f), tbY, Sf(104f), tbH);
            _rcToggleEmpty = new RectangleF(w - pad - Sf(146f), tbY, Sf(146f), tbH);

            // List panel
            int listTop = S(176f);
            int footerH = S(66f);
            int listH = Math.Max(S(120f), h - listTop - footerH - S(8f));
            _listPanel.SetBounds(S(20f), listTop, Math.Max(S(300f), w - S(40f)), listH);

            // Footer buttons
            float btnH = Sf(36f);
            float btnY = h - Sf(51f);
            float cleanW = Sf(208f);
            float copyW = Sf(126f);
            _rcClean = new RectangleF(w - pad - cleanW, btnY, cleanW, btnH);
            _rcCopyReport = new RectangleF(_rcClean.X - Sf(10f) - copyW, btnY, copyW, btnH);
        }

        private void UpdateTitleText()
        {
            Text = _prefs.IsTurkish
                ? "SafeSweep — Güvenli Windows Disk Temizleyici"
                : "SafeSweep — Safe Windows Disk Cleaner";
        }

        private void RefreshDiskSpace()
        {
            if (_launch.DemoMode)
            {
                _diskName = "C:";
                _diskFreeBytes = 148L * 1024 * 1024 * 1024 + 420L * 1024 * 1024;
                _diskTotalBytes = 476L * 1024 * 1024 * 1024;
                return;
            }

            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                DriveInfo di = new DriveInfo(root);
                if (di.IsReady)
                {
                    _diskName = root.TrimEnd('\\');
                    _diskFreeBytes = di.AvailableFreeSpace;
                    _diskTotalBytes = di.TotalSize;
                }
            }
            catch
            {
            }
        }

        private void LoadDemoData()
        {
            _allCategories = new List<CleanupCategory>();

            _allCategories.Add(MakeDemoCat(
                "user_temp", "User Temporary Files", "Kullanıcı Geçici Dosyaları (Temp)",
                "App scratch & installer leftovers. Files modified in the last 15 min are skipped.",
                "Uygulama ve kurulum artıkları. Son 15 dk içinde kullanılan dosyalar korunur.",
                @"C:\Users\Alex\AppData\Local\Temp", SafetyTier.Safe,
                712L * 1024 * 1024, 1428, true, false,
                new ScannedFileEntry[]
                {
                    DemoFile(@"C:\Users\Alex\AppData\Local\Temp\setup_bundle_2026.tmp", "setup_bundle_2026.tmp", 198L * 1024 * 1024, -4),
                    DemoFile(@"C:\Users\Alex\AppData\Local\Temp\update_pkg_cache.cab", "update_pkg_cache.cab", 124L * 1024 * 1024, -18),
                    DemoFile(@"C:\Users\Alex\AppData\Local\Temp\installer_extract.bin", "installer_extract.bin", 86L * 1024 * 1024, -42),
                    DemoFile(@"C:\Users\Alex\AppData\Local\Temp\diag_trace_04.etl", "diag_trace_04.etl", 52L * 1024 * 1024, -76),
                    DemoFile(@"C:\Users\Alex\AppData\Local\Temp\archive_unpack.dat", "archive_unpack.dat", 39L * 1024 * 1024, -120)
                }));

            _allCategories.Add(MakeDemoCat(
                "chrome_cache", "Google Chrome Cache", "Google Chrome Önbelleği",
                "Web & GPU cache only. Cookies, logins, history, bookmarks & extensions are untouched.",
                "Yalnızca web ve GPU önbelleği. Çerez, şifre, geçmiş ve eklentiler korunur.",
                @"C:\Users\Alex\AppData\Local\Google\Chrome\User Data", SafetyTier.Safe,
                584L * 1024 * 1024, 3214, true, false,
                new ScannedFileEntry[]
                {
                    DemoFile(@"C:\Users\Alex\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_003a19", "f_003a19", 44L * 1024 * 1024, -2),
                    DemoFile(@"C:\Users\Alex\AppData\Local\Google\Chrome\User Data\Default\Code Cache\js\b7c92a01", "b7c92a01", 28L * 1024 * 1024, -5)
                }));

            _allCategories.Add(MakeDemoCat(
                "dev_caches", "Developer Package Caches", "Geliştirici Paket Önbellekleri",
                "npm, pip, Yarn & NuGet download caches. Packages re-download cleanly if needed.",
                "npm, pip, Yarn ve NuGet indirme önbellekleri. Gerektiğinde tekrar indirilir.",
                @"npm-cache · pip\Cache · NuGet", SafetyTier.Safe,
                346L * 1024 * 1024, 1892, true, false, null));

            _allCategories.Add(MakeDemoCat(
                "wer_dumps", "Crash Dumps & Error Reports", "Hata Raporları ve Çökme Dökümleri",
                "Old Windows Error Reporting logs and application crash dumps (.dmp).",
                "Eski Windows hata raporları ve uygulama çökme döküm dosyaları (.dmp).",
                @"C:\Users\Alex\AppData\Local\CrashDumps  +  WER", SafetyTier.Safe,
                164L * 1024 * 1024, 27, true, false, null));

            _allCategories.Add(MakeDemoCat(
                "recycle_bin", "Windows Recycle Bin", "Geri Dönüşüm Kutusu",
                "Permanently removes previously deleted items across all drives. Check before emptying.",
                "Tüm sürücülerdeki silinmiş öğeleri kalıcı olarak boşaltır. Önce kontrol edin.",
                "$Recycle.Bin (All Drives)", SafetyTier.Caution,
                420L * 1024 * 1024, 64, false, true, null));

            _allCategories.Add(MakeDemoCat(
                "gpu_shader_cache", "DirectX & GPU Shader Cache", "DirectX ve GPU Shader Önbelleği",
                "Compiled graphics shaders (DX/AMD/NVIDIA). Games may stutter briefly while rebuilding.",
                "Derlenmiş grafik shader'ları. Oyunlar ilk açılışta kısa süre yeniden derler.",
                @"C:\Users\Alex\AppData\Local\D3DSCache  +  GPU Cache", SafetyTier.Caution,
                312L * 1024 * 1024, 148, false, false, null));

            _statusText = _prefs.IsTurkish
                ? "Tarama tamamlandı. Dosyaları görmek için herhangi bir satırda 'İncele'ye tıklayın."
                : "Scan complete. Click 'Inspect' on any row to preview files before cleaning.";
            _statusIsSuccess = false;
            RefreshVisibleList();
        }

        private static ScannedFileEntry DemoFile(string fullPath, string name, long bytes, int hoursAgo)
        {
            ScannedFileEntry e = new ScannedFileEntry();
            e.FullPath = fullPath;
            e.FileName = name;
            e.SizeBytes = bytes;
            e.LastWriteTime = DateTime.Now.AddHours(hoursAgo);
            return e;
        }

        private static CleanupCategory MakeDemoCat(
            string id, string nameEn, string nameTr, string descEn, string descTr,
            string displayPath, SafetyTier tier, long bytes, long files, bool isChecked, bool isBin,
            ScannedFileEntry[] topFiles)
        {
            CleanupCategory cat = new CleanupCategory();
            cat.Id = id;
            cat.NameEn = nameEn;
            cat.NameTr = nameTr;
            cat.DescEn = descEn;
            cat.DescTr = descTr;
            cat.DisplayPath = displayPath;
            cat.Tier = tier;
            cat.TotalBytes = bytes;
            cat.FileCount = files;
            cat.Checked = isChecked;
            cat.HasScanned = true;
            cat.IsRecycleBin = isBin;

            TargetFolderStats st = new TargetFolderStats();
            st.Path = displayPath;
            st.Bytes = bytes;
            st.FileCount = (int)files;
            st.Exists = true;
            cat.FolderBreakdowns.Add(st);

            if (topFiles != null)
                cat.TopFiles.AddRange(topFiles);

            return cat;
        }

        private void StartScanAsync()
        {
            if (_isBusy) return;
            _isBusy = true;
            _cancelFlag = false;
            _progressRatio = 0f;
            _statusIsSuccess = false;
            _statusText = _prefs.IsTurkish ? "Sistem klasörleri taranıyor (salt okunur)..." : "Scanning system folders (read-only)...";
            _listPanel.SetBusy(true);
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<CleanupCategory> catalog = CleanupEngine.BuildCatalog();
                int total = catalog.Count;

                for (int i = 0; i < total; i++)
                {
                    if (_cancelFlag || IsDisposed)
                        return;

                    CleanupCategory cat = catalog[i];
                    int idx = i;
                    SafeUiInvoke(delegate
                    {
                        _progressRatio = (float)idx / Math.Max(1, total);
                        _statusText = (_prefs.IsTurkish ? "Taranıyor: " : "Scanning: ") + cat.GetName(_prefs.IsTurkish);
                        Invalidate();
                    });

                    CleanupEngine.ScanCategory(cat, delegate { return _cancelFlag || IsDisposed; });
                }

                // Sort: SAFE first (largest to smallest), then CAUTION (largest to smallest)
                catalog.Sort(delegate(CleanupCategory a, CleanupCategory b)
                {
                    int tierCmp = a.Tier.CompareTo(b.Tier);
                    if (tierCmp != 0) return tierCmp;
                    return b.TotalBytes.CompareTo(a.TotalBytes);
                });

                SafeUiInvoke(delegate
                {
                    _allCategories = catalog;
                    _isBusy = false;
                    _progressRatio = 1f;
                    _listPanel.SetBusy(false);
                    RefreshDiskSpace();
                    _statusText = _prefs.IsTurkish
                        ? "Tarama tamamlandı. Temizlemeden önce 'İncele' ile klasör içeriğini görebilirsiniz."
                        : "Scan complete. Click 'Inspect' on any category to inspect files before cleaning.";
                    RefreshVisibleList();
                });
            });
        }

        private void StartCleanAsync(List<CleanupCategory> selected)
        {
            if (_isBusy || _launch.DemoMode) return;
            _isBusy = true;
            _cancelFlag = false;
            _progressRatio = 0f;
            _statusIsSuccess = false;
            _listPanel.SetBusy(true);
            Invalidate();

            ThreadPool.QueueUserWorkItem(delegate
            {
                CleanSummary summary = CleanupEngine.CleanSelected(
                    _allCategories,
                    delegate(string curName, int done, int total)
                    {
                        SafeUiInvoke(delegate
                        {
                            _progressRatio = (float)done / Math.Max(1, total);
                            if (!string.IsNullOrEmpty(curName))
                                _statusText = (_prefs.IsTurkish ? "Temizleniyor: " : "Cleaning: ") + curName;
                            Invalidate();
                        });
                    },
                    delegate { return _cancelFlag || IsDisposed; });

                SafeUiInvoke(delegate
                {
                    _isBusy = false;
                    _progressRatio = 1f;
                    _listPanel.SetBusy(false);
                    RefreshDiskSpace();

                    CultureInfo c = CleanupEngine.GetCulture(_prefs.IsTurkish);
                    string freedStr = CleanupEngine.FormatBytes(summary.TotalFreedBytes, _prefs.IsTurkish);
                    if (_prefs.IsTurkish)
                    {
                        _statusText = string.Format(
                            c,
                            "Temizlendi: {0} kazanıldı · {1:N0} dosya silindi · {2:N0} aktif/kilitli dosya korundu.",
                            freedStr, summary.TotalDeletedFiles, summary.TotalSkippedFiles);
                    }
                    else
                    {
                        _statusText = string.Format(
                            c,
                            "Done: Freed {0} · {1:N0} files removed · {2:N0} in-use/protected files safely skipped.",
                            freedStr, summary.TotalDeletedFiles, summary.TotalSkippedFiles);
                    }
                    _statusIsSuccess = true;
                    RefreshVisibleList();
                });
            });
        }

        private void SafeUiInvoke(MethodInvoker action)
        {
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(action);
            }
            catch
            {
            }
        }

        private void RefreshVisibleList()
        {
            List<CleanupCategory> visible = new List<CleanupCategory>();
            foreach (CleanupCategory cat in _allCategories)
            {
                if (_showEmpty || cat.TotalBytes > 0)
                    visible.Add(cat);
            }
            _listPanel.SetItems(visible);
            Invalidate();
        }

        private int CountEmptyCategories()
        {
            int n = 0;
            foreach (CleanupCategory cat in _allCategories)
            {
                if (cat.TotalBytes == 0) n++;
            }
            return n;
        }

        private List<CleanupCategory> GetSelectedCleanable()
        {
            List<CleanupCategory> res = new List<CleanupCategory>();
            foreach (CleanupCategory cat in _allCategories)
            {
                if (cat.Checked && cat.TotalBytes > 0)
                    res.Add(cat);
            }
            return res;
        }

        private void OnInspectCategory(CleanupCategory cat)
        {
            if (cat == null) return;
            using (FolderInspectorDialog dlg = new FolderInspectorDialog(cat, _prefs.IsTurkish))
                dlg.ShowDialog(this);
        }

        private void OnCleanClicked()
        {
            if (_isBusy) return;
            List<CleanupCategory> selected = GetSelectedCleanable();
            if (selected.Count == 0) return;

            using (ConfirmCleanDialog dlg = new ConfirmCleanDialog(selected, _prefs.IsTurkish))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    if (_launch.DemoMode) return;
                    StartCleanAsync(selected);
                }
            }
        }

        private void ToggleLanguage()
        {
            _prefs.Language = _prefs.IsTurkish ? "en" : "tr";
            if (!_launch.DemoMode)
                _prefs.Save();
            UpdateTitleText();
            _listPanel.SetLanguage(_prefs.IsTurkish);
            if (!_isBusy && !_statusIsSuccess)
            {
                _statusText = _prefs.IsTurkish
                    ? "Tarama tamamlandı. Temizlemeden önce 'İncele' ile klasör içeriğini görebilirsiniz."
                    : "Scan complete. Click 'Inspect' on any category to inspect files before cleaning.";
            }
            Invalidate();
        }

        private void ElevateAsAdmin()
        {
            if (_isAdmin || _launch.DemoMode) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Close();
            }
            catch
            {
                // User cancelled UAC prompt; stay running cleanly as standard user.
            }
        }

        private void SelectSafeOnly()
        {
            if (_isBusy) return;
            foreach (CleanupCategory cat in _allCategories)
                cat.Checked = (cat.Tier == SafetyTier.Safe && cat.TotalBytes > 0);
            _listPanel.Invalidate();
            Invalidate();
        }

        private void SelectNone()
        {
            if (_isBusy) return;
            foreach (CleanupCategory cat in _allCategories)
                cat.Checked = false;
            _listPanel.Invalidate();
            Invalidate();
        }

        private void CopyReportToClipboard()
        {
            try
            {
                bool tr = _prefs.IsTurkish;
                CultureInfo c = CleanupEngine.GetCulture(tr);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(tr ? "=== SafeSweep Disk Analiz Raporu ===" : "=== SafeSweep Disk Breakdown Report ===");

                long grandBytes = 0;
                long grandFiles = 0;
                foreach (CleanupCategory cat in _allCategories)
                {
                    if (cat.TotalBytes <= 0) continue;
                    grandBytes += cat.TotalBytes;
                    grandFiles += cat.FileCount;
                    string tag = cat.Tier == SafetyTier.Safe ? (tr ? "GÜVENLİ" : "SAFE") : (tr ? "DİKKAT" : "CAUTION");
                    sb.AppendFormat(
                        c,
                        "[{0}] {1}: {2} ({3:N0} {4}) — {5}",
                        tag,
                        cat.GetName(tr),
                        CleanupEngine.FormatBytes(cat.TotalBytes, tr),
                        cat.FileCount,
                        tr ? "dosya" : "files",
                        cat.DisplayPath);
                    sb.AppendLine();
                }

                sb.AppendFormat(
                    c,
                    tr ? "Toplam Bulunan: {0} ({1:N0} dosya)" : "Total Reclaimable: {0} ({1:N0} files)",
                    CleanupEngine.FormatBytes(grandBytes, tr),
                    grandFiles);
                sb.AppendLine();

                Clipboard.SetText(sb.ToString());
                _copiedUntil = DateTime.UtcNow.AddMilliseconds(1600);
                Invalidate();
            }
            catch
            {
            }
        }

        private int HitTest(Point pt)
        {
            if (_rcLang.Contains(pt)) return HitLang;
            if (!_isAdmin && _rcAdmin.Contains(pt)) return HitAdmin;
            if (!_isBusy && _rcRescan.Contains(pt)) return HitRescan;
            if (!_isBusy && _rcSelSafe.Contains(pt)) return HitSelSafe;
            if (!_isBusy && _rcSelNone.Contains(pt)) return HitSelNone;
            if (_rcToggleEmpty.Contains(pt)) return HitToggleEmpty;
            if (_rcCopyReport.Contains(pt)) return HitCopyReport;
            if (!_isBusy && GetSelectedCleanable().Count > 0 && _rcClean.Contains(pt)) return HitClean;
            return HitNone;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitTest(e.Location);
            if (hit != _hoverHit)
            {
                _hoverHit = hit;
                Cursor = hit != HitNone ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverHit != HitNone || _pressHit != HitNone)
            {
                _hoverHit = HitNone;
                _pressHit = HitNone;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                int hit = HitTest(e.Location);
                if (hit != _pressHit)
                {
                    _pressHit = hit;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            int released = HitTest(e.Location);
            int pressed = _pressHit;
            _pressHit = HitNone;

            if (pressed != HitNone && pressed == released)
            {
                if (pressed == HitLang) ToggleLanguage();
                else if (pressed == HitAdmin) ElevateAsAdmin();
                else if (pressed == HitRescan) { if (!_launch.DemoMode) StartScanAsync(); }
                else if (pressed == HitSelSafe) SelectSafeOnly();
                else if (pressed == HitSelNone) SelectNone();
                else if (pressed == HitToggleEmpty) { _showEmpty = !_showEmpty; RefreshVisibleList(); }
                else if (pressed == HitCopyReport) CopyReportToClipboard();
                else if (pressed == HitClean) OnCleanClicked();
            }
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F5 && !_isBusy && !_launch.DemoMode)
            {
                StartScanAsync();
                e.Handled = true;
            }
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.L)
            {
                ToggleLanguage();
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(ColBg);

            DrawHeader(g);
            DrawSummaryCards(g);
            DrawToolbar(g);
            DrawFooter(g);
        }

        private void DrawHeader(Graphics g)
        {
            bool tr = _prefs.IsTurkish;
            float pad = Sf(20f);

            // Shield glyph + Brand
            DrawShieldIcon(g, new RectangleF(pad, Sf(16f), Sf(18f), Sf(20f)), ColSafe);

            using (SolidBrush wb = new SolidBrush(ColTextPrimary))
            using (SolidBrush mb = new SolidBrush(ColTextMuted))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Alignment = StringAlignment.Near;
                g.DrawString("SAFESWEEP", _fontBrand, wb, new RectangleF(pad + Sf(24f), Sf(13f), Sf(110f), Sf(26f)), sf);
                g.DrawString(
                    tr ? "Güvenli Windows Disk Temizleyici" : "Safe Windows Disk Cleaner",
                    _fontTagline,
                    mb,
                    new RectangleF(pad + Sf(126f), Sf(14f), Sf(240f), Sf(25f)),
                    sf);
            }

            // Disk C: capacity pill
            if (_diskTotalBytes > 0)
            {
                string diskTxt = tr
                    ? string.Format("{0}  {1} boş / {2}", _diskName, CleanupEngine.FormatBytes(_diskFreeBytes, true), CleanupEngine.FormatBytes(_diskTotalBytes, true))
                    : string.Format("{0}  {1} free / {2}", _diskName, CleanupEngine.FormatBytes(_diskFreeBytes, false), CleanupEngine.FormatBytes(_diskTotalBytes, false));

                RectangleF diskRc = new RectangleF(_rcAdmin.X - Sf(198f), Sf(15f), Sf(190f), Sf(23f));
                if (diskRc.X > Sf(350f))
                {
                    DrawRoundedBox(g, diskRc, Sf(6f), ColCard, ColCardBorder);
                    using (SolidBrush db = new SolidBrush(ColTextSecondary))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags = StringFormatFlags.NoWrap;
                        g.DrawString(diskTxt, _fontSmallPill, db, diskRc, sf);
                    }
                }
            }

            // Admin badge / elevate button
            bool admHover = !_isAdmin && _hoverHit == HitAdmin;
            Color admBg = _isAdmin ? Color.FromArgb(18, 46, 36) : (admHover ? ColBtnHover : ColCard);
            Color admBd = _isAdmin ? Color.FromArgb(38, 128, 96) : (admHover ? ColSafe : ColCardBorder);
            Color admFg = _isAdmin ? ColSafe : (admHover ? ColTextPrimary : ColTextSecondary);
            DrawRoundedBox(g, _rcAdmin, Sf(6f), admBg, admBd);
            using (SolidBrush ab = new SolidBrush(admFg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                string admText = _isAdmin
                    ? (tr ? "Yönetici" : "Admin")
                    : (tr ? "Yönetici ↗" : "Admin ↗");
                g.DrawString(admText, _fontSmallPill, ab, _rcAdmin, sf);
            }

            // TR | EN toggle pill
            bool langHover = _hoverHit == HitLang;
            DrawRoundedBox(g, _rcLang, Sf(6f), langHover ? ColBtnHover : ColCard, langHover ? Color.FromArgb(72, 85, 104) : ColCardBorder);
            float halfW = _rcLang.Width * 0.5f;
            RectangleF trRc = new RectangleF(_rcLang.X + Sf(2f), _rcLang.Y, halfW - Sf(2f), _rcLang.Height);
            RectangleF enRc = new RectangleF(_rcLang.X + halfW, _rcLang.Y, halfW - Sf(2f), _rcLang.Height);
            using (SolidBrush tb = new SolidBrush(tr ? ColSafe : ColTextMuted))
            using (SolidBrush eb = new SolidBrush(!tr ? ColSafe : ColTextMuted))
            using (Pen sep = new Pen(ColDivider, 1f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("TR", _fontSmallPill, tb, trRc, sf);
                g.DrawString("EN", _fontSmallPill, eb, enRc, sf);
                g.DrawLine(sep, _rcLang.X + halfW, _rcLang.Y + Sf(4f), _rcLang.X + halfW, _rcLang.Bottom - Sf(4f));
            }
        }

        private void DrawSummaryCards(Graphics g)
        {
            bool tr = _prefs.IsTurkish;
            CultureInfo c = CleanupEngine.GetCulture(tr);

            long totalBytes = 0, totalFiles = 0;
            long selBytes = 0, selFiles = 0;
            int nonEmptyCount = 0, selCount = 0, selCautionCount = 0;

            foreach (CleanupCategory cat in _allCategories)
            {
                totalBytes += cat.TotalBytes;
                totalFiles += cat.FileCount;
                if (cat.TotalBytes > 0) nonEmptyCount++;
                if (cat.Checked && cat.TotalBytes > 0)
                {
                    selBytes += cat.TotalBytes;
                    selFiles += cat.FileCount;
                    selCount++;
                    if (cat.Tier == SafetyTier.Caution) selCautionCount++;
                }
            }

            float pad = Sf(20f);
            float gap = Sf(10f);
            float usableW = ClientSize.Width - pad * 2f;
            float y = Sf(50f);
            float h = Sf(76f);

            float w1 = (usableW - gap * 2f) * 0.35f;
            float w2 = (usableW - gap * 2f) * 0.35f;
            float w3 = usableW - gap * 2f - w1 - w2;

            // Card 1: Total Found
            RectangleF c1 = new RectangleF(pad, y, w1, h);
            DrawSummaryCard(
                g, c1,
                tr ? "TOPLAM BULUNAN" : "TOTAL RECLAIMABLE",
                CleanupEngine.FormatBytes(totalBytes, tr),
                ColCyan,
                tr
                    ? string.Format(c, "{0:N0} dosya · {1} konum", totalFiles, nonEmptyCount)
                    : string.Format(c, "{0:N0} files · {1} locations", totalFiles, nonEmptyCount));

            // Card 2: Selected to Clean
            RectangleF c2 = new RectangleF(c1.Right + gap, y, w2, h);
            string selSub;
            if (selCount == 0)
                selSub = tr ? "Temizlemek için öğe seçin" : "Select categories to clean";
            else if (selCautionCount > 0)
                selSub = tr
                    ? string.Format(c, "{0} öğe ({1} dikkat) · {2:N0} dosya", selCount, selCautionCount, selFiles)
                    : string.Format(c, "{0} items ({1} caution) · {2:N0} files", selCount, selCautionCount, selFiles);
            else
                selSub = tr
                    ? string.Format(c, "{0} güvenli öğe · {1:N0} dosya", selCount, selFiles)
                    : string.Format(c, "{0} safe items · {1:N0} files", selCount, selFiles);

            DrawSummaryCard(
                g, c2,
                tr ? "SEÇİLEN ALAN" : "SELECTED TO CLEAN",
                CleanupEngine.FormatBytes(selBytes, tr),
                selCautionCount > 0 ? ColAmber : ColSafe,
                selSub);

            // Card 3: Safety Protection
            RectangleF c3 = new RectangleF(c2.Right + gap, y, w3, h);
            DrawSummaryCard(
                g, c3,
                tr ? "GÜVENLİK KORUMASI" : "SAFETY GUARD",
                tr ? "Aktif" : "Active",
                ColSafe,
                tr ? "Symlink, şifre ve aktif dosyalar korunur" : "Symlinks, logins & active files safe");
        }

        private void DrawSummaryCard(Graphics g, RectangleF r, string label, string value, Color valueColor, string sub)
        {
            DrawRoundedBox(g, r, Sf(10f), ColCard, ColCardBorder);
            float px = Sf(14f);

            using (SolidBrush lb = new SolidBrush(ColTextMuted))
            using (SolidBrush vb = new SolidBrush(valueColor))
            using (SolidBrush sb = new SolidBrush(ColTextSecondary))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;

                g.DrawString(label, _fontCardLabel, lb, new RectangleF(r.X + px, r.Y + Sf(8f), r.Width - px * 2f, Sf(15f)), sf);
                g.DrawString(value, _fontCardBig, vb, new RectangleF(r.X + px, r.Y + Sf(23f), r.Width - px * 2f, Sf(28f)), sf);
                g.DrawString(sub, _fontCardSub, sb, new RectangleF(r.X + px, r.Y + Sf(52f), r.Width - px * 2f, Sf(17f)), sf);
            }
        }

        private void DrawToolbar(Graphics g)
        {
            bool tr = _prefs.IsTurkish;
            DrawSmallButton(g, _rcRescan, HitRescan, tr ? "Yeniden Tara" : "Rescan", !_isBusy, false);
            DrawSmallButton(g, _rcSelSafe, HitSelSafe, tr ? "Güvenlileri Seç" : "Select Safe", !_isBusy, false);
            DrawSmallButton(g, _rcSelNone, HitSelNone, tr ? "Seçimi Temizle" : "Select None", !_isBusy, false);

            int emptyCnt = CountEmptyCategories();
            string emptyTxt = _showEmpty
                ? (tr ? "Boşları Gizle" : "Hide Empty")
                : (tr ? string.Format("Boşları Göster ({0})", emptyCnt) : string.Format("Show Empty ({0})", emptyCnt));
            DrawSmallButton(g, _rcToggleEmpty, HitToggleEmpty, emptyTxt, true, _showEmpty);
        }

        private void DrawSmallButton(Graphics g, RectangleF r, int hitId, string text, bool enabled, bool active)
        {
            bool hov = enabled && _hoverHit == hitId;
            bool prs = enabled && _pressHit == hitId;

            Color bg = !enabled ? ColCard : (prs ? ColBtnPress : (hov ? ColBtnHover : (active ? Color.FromArgb(24, 42, 38) : ColBtnNormal)));
            Color bd = !enabled ? ColDivider : (active ? Color.FromArgb(42, 130, 98) : (hov ? Color.FromArgb(72, 86, 106) : ColBtnBorder));
            Color fg = !enabled ? ColTextMuted : (active ? ColSafe : ColTextBody);

            DrawRoundedBox(g, r, Sf(7f), bg, bd);
            using (SolidBrush fb = new SolidBrush(fg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text, _fontToolbar, fb, r, sf);
            }
        }

        private void DrawFooter(Graphics g)
        {
            bool tr = _prefs.IsTurkish;
            int h = ClientSize.Height;
            float footerTop = h - Sf(64f);

            using (Pen p = new Pen(ColDivider, 1f))
                g.DrawLine(p, 0, footerTop, ClientSize.Width, footerTop);

            float statusMaxW = Math.Max(Sf(120f), _rcCopyReport.X - Sf(32f));
            if (_isBusy)
            {
                // Live progress bar + status label
                RectangleF lblRc = new RectangleF(Sf(20f), footerTop + Sf(12f), statusMaxW, Sf(18f));
                using (SolidBrush sb = new SolidBrush(ColTextBody))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(_statusText, _fontStatus, sb, lblRc, sf);
                }

                RectangleF track = new RectangleF(Sf(20f), footerTop + Sf(36f), Math.Min(Sf(320f), statusMaxW), Sf(6f));
                using (GraphicsPath tp = CreateRoundedRect(track, Sf(3f)))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(30, 38, 48)))
                    g.FillPath(tb, tp);

                float fillW = Math.Max(Sf(14f), track.Width * Math.Min(1f, Math.Max(0.06f, _progressRatio)));
                using (GraphicsPath fp = CreateRoundedRect(new RectangleF(track.X, track.Y, fillW, track.Height), Sf(3f)))
                using (SolidBrush fb = new SolidBrush(ColSafe))
                    g.FillPath(fb, fp);
            }
            else
            {
                RectangleF lblRc = new RectangleF(Sf(20f), footerTop + Sf(12f), statusMaxW, Sf(40f));
                using (SolidBrush sb = new SolidBrush(_statusIsSuccess ? ColSafe : ColTextSecondary))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(_statusText, _fontStatus, sb, lblRc, sf);
                }
            }

            // Copy Report button
            bool copied = _copiedUntil != DateTime.MinValue && DateTime.UtcNow < _copiedUntil;
            string copyTxt = copied
                ? (tr ? "Kopyalandı ✓" : "Copied ✓")
                : (tr ? "Raporu Kopyala" : "Copy Report");
            DrawSmallButton(g, _rcCopyReport, HitCopyReport, copyTxt, true, copied);

            // Primary Clean Selected button
            List<CleanupCategory> sel = GetSelectedCleanable();
            long selBytes = 0;
            foreach (CleanupCategory cat in sel) selBytes += cat.TotalBytes;

            bool canClean = !_isBusy && sel.Count > 0;
            bool cleanHov = canClean && _hoverHit == HitClean;
            bool cleanPrs = canClean && _pressHit == HitClean;

            Color cBg = !canClean ? ColPrimaryDisabled : (cleanPrs ? Color.FromArgb(14, 98, 70) : (cleanHov ? ColPrimaryHover : ColPrimaryBg));
            Color cBd = !canClean ? ColDivider : ColSafe;
            Color cFg = !canClean ? ColTextMuted : Color.White;

            DrawRoundedBox(g, _rcClean, Sf(8f), cBg, cBd);
            string cleanTxt;
            if (sel.Count > 0)
            {
                string sz = CleanupEngine.FormatBytes(selBytes, tr);
                cleanTxt = tr ? string.Format("Temizle ({0})", sz) : string.Format("Clean Selected ({0})", sz);
            }
            else
            {
                cleanTxt = tr ? "Seçilenleri Temizle" : "Clean Selected";
            }

            using (SolidBrush fb = new SolidBrush(cFg))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(cleanTxt, _fontPrimaryBtn, fb, _rcClean, sf);
            }
        }

        private static void DrawShieldIcon(Graphics g, RectangleF r, Color color)
        {
            PointF[] pts = new PointF[]
            {
                new PointF(r.X + r.Width * 0.50f, r.Y),
                new PointF(r.Right, r.Y + r.Height * 0.20f),
                new PointF(r.Right - r.Width * 0.08f, r.Y + r.Height * 0.62f),
                new PointF(r.X + r.Width * 0.50f, r.Bottom),
                new PointF(r.X + r.Width * 0.08f, r.Y + r.Height * 0.62f),
                new PointF(r.X, r.Y + r.Height * 0.20f)
            };
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, color)))
            using (Pen stroke = new Pen(color, 1.5f))
            {
                stroke.LineJoin = LineJoin.Round;
                g.FillPolygon(fill, pts);
                g.DrawPolygon(stroke, pts);
            }

            using (Pen chk = new Pen(color, 1.6f))
            {
                chk.StartCap = LineCap.Round;
                chk.EndCap = LineCap.Round;
                chk.LineJoin = LineJoin.Round;
                PointF[] cPts = new PointF[]
                {
                    new PointF(r.X + r.Width * 0.28f, r.Y + r.Height * 0.50f),
                    new PointF(r.X + r.Width * 0.46f, r.Y + r.Height * 0.67f),
                    new PointF(r.X + r.Width * 0.74f, r.Y + r.Height * 0.36f)
                };
                g.DrawLines(chk, cPts);
            }
        }

        private static void DrawRoundedBox(Graphics g, RectangleF r, float radius, Color fill, Color border)
        {
            using (GraphicsPath p = CreateRoundedRect(r, radius))
            using (SolidBrush fb = new SolidBrush(fill))
            using (Pen bp = new Pen(border, 1f))
            {
                g.FillPath(fb, p);
                g.DrawPath(bp, p);
            }
        }

        private static GraphicsPath CreateRoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cancelFlag = true;
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Dispose();
            }
            if (_appIcon != null)
                _appIcon.Dispose();
            DisposeFonts();
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            NativeWindowStyle.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(LaunchArgs.Parse(args)));
        }
    }
}
