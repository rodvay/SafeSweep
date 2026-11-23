using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SafeSweep
{
    internal static class NativeWindowStyle
    {
        public const int WM_DPICHANGED = 0x02E0;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;

        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static void ApplyDarkTitleBar(IntPtr hwnd, Color captionColor, Color textColor)
        {
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, sizeof(int));

                int capRef = captionColor.R | (captionColor.G << 8) | (captionColor.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref capRef, sizeof(int));

                int txtRef = textColor.R | (textColor.G << 8) | (textColor.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txtRef, sizeof(int));
            }
            catch { }
        }
    }

    internal sealed class FolderInspectorDialog : Form
    {
        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColSurface = Color.FromArgb(22, 27, 34);
        private static readonly Color ColBorder = Color.FromArgb(48, 58, 72);
        private static readonly Color ColText = Color.FromArgb(240, 246, 252);
        private static readonly Color ColBody = Color.FromArgb(204, 212, 220);
        private static readonly Color ColMuted = Color.FromArgb(139, 148, 158);
        private static readonly Color ColSafe = Color.FromArgb(52, 211, 153);
        private static readonly Color ColCaution = Color.FromArgb(251, 191, 36);

        private readonly CleanupCategory _cat;
        private readonly string _lang;
        private readonly ListView _lvFolders;
        private readonly ListView _lvFiles;
        private readonly List<Font> _ownedFonts = new List<Font>();

        private Font TrackFont(Font f) { _ownedFonts.Add(f); return f; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Font f in _ownedFonts) { try { f.Dispose(); } catch { } }
                _ownedFonts.Clear();
            }
            base.Dispose(disposing);
        }

        public FolderInspectorDialog(CleanupCategory cat, string lang)
        {
            _cat = cat;
            _lang = lang;
            CultureInfo c = LangHelper.GetCulture(lang);
            string fontName = LangHelper.UiFontFamily(lang);

            Text = LangHelper.T(lang, "Folder Breakdown", "Klasör Dökümü", "文件夹详情") + " — " + cat.GetName(lang);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = TrackFont(new Font(fontName, 9f, FontStyle.Regular));

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(650, 496);

            Label lblTitle = new Label();
            lblTitle.UseMnemonic = false;
            lblTitle.Text = cat.GetName(lang);
            lblTitle.Font = TrackFont(new Font(fontName, 12.5f, FontStyle.Bold));
            lblTitle.ForeColor = ColText;
            lblTitle.Location = new Point(20, 16);
            lblTitle.AutoSize = true;
            Controls.Add(lblTitle);

            Label lblSummary = new Label();
            lblSummary.UseMnemonic = false;
            lblSummary.Text = string.Format(
                c, "{0}  ·  {1:N0} {2}",
                CleanupEngine.FormatBytes(cat.TotalBytes, lang),
                cat.FileCount,
                LangHelper.T(lang, "files", "dosya", "个文件"));
            lblSummary.Font = TrackFont(new Font(fontName, 10.5f, FontStyle.Bold));
            lblSummary.ForeColor = cat.Tier == SafetyTier.Safe ? ColSafe : ColCaution;
            lblSummary.Location = new Point(400, 18);
            lblSummary.Size = new Size(230, 22);
            lblSummary.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(lblSummary);

            Label lblDesc = new Label();
            lblDesc.UseMnemonic = false;
            lblDesc.Text = cat.GetDescription(lang);
            lblDesc.ForeColor = ColMuted;
            lblDesc.Location = new Point(20, 44);
            lblDesc.Size = new Size(610, 22);
            Controls.Add(lblDesc);

            Label lblFolders = new Label();
            lblFolders.UseMnemonic = false;
            lblFolders.Text = LangHelper.T(lang, "Scanned Folders:", "Taranan Klasörler:", "扫描目录:");
            lblFolders.Font = TrackFont(new Font(fontName, 8.8f, FontStyle.Bold));
            lblFolders.ForeColor = ColBody;
            lblFolders.Location = new Point(20, 74);
            lblFolders.AutoSize = true;
            Controls.Add(lblFolders);

            _lvFolders = CreateThemedDarkListView(new Point(20, 96), new Size(610, 128), ColSurface, ColBody, fontName);
            _lvFolders.Columns.Add(LangHelper.T(lang, "Folder Path", "Klasör Yolu", "文件夹路径"), 420);
            _lvFolders.Columns.Add(LangHelper.T(lang, "Files", "Dosya", "文件数"), 78, HorizontalAlignment.Right);
            _lvFolders.Columns.Add(LangHelper.T(lang, "Size", "Boyut", "大小"), 110, HorizontalAlignment.Right);

            foreach (TargetFolderStats st in cat.FolderBreakdowns)
            {
                ListViewItem lvi = new ListViewItem(st.Path);
                lvi.SubItems.Add(st.FileCount.ToString("N0", c));
                lvi.SubItems.Add(CleanupEngine.FormatBytes(st.Bytes, lang));
                lvi.Tag = st.Path;
                _lvFolders.Items.Add(lvi);
            }
            _lvFolders.DoubleClick += delegate { OpenSelectedFolder(); };
            Controls.Add(_lvFolders);

            Label lblFiles = new Label();
            lblFiles.UseMnemonic = false;
            lblFiles.Text = LangHelper.T(
                lang,
                "Largest Files (Top 25 — Read-Only Preview):",
                "En Büyük Dosyalar (İlk 25 — Salt Okunur Önizleme):",
                "体积最大的文件 (前 25 项 — 只读预览):");
            lblFiles.Font = TrackFont(new Font(fontName, 8.8f, FontStyle.Bold));
            lblFiles.ForeColor = ColBody;
            lblFiles.Location = new Point(20, 236);
            lblFiles.AutoSize = true;
            Controls.Add(lblFiles);

            _lvFiles = CreateThemedDarkListView(new Point(20, 258), new Size(610, 178), ColSurface, ColBody, fontName);
            _lvFiles.Columns.Add(LangHelper.T(lang, "File Name", "Dosya Adı", "文件名"), 365);
            _lvFiles.Columns.Add(LangHelper.T(lang, "Last Modified", "Son Değişiklik", "修改时间"), 132);
            _lvFiles.Columns.Add(LangHelper.T(lang, "Size", "Boyut", "大小"), 111, HorizontalAlignment.Right);

            if (cat.IsRecycleBin)
            {
                ListViewItem lvi = new ListViewItem(LangHelper.T(
                    lang,
                    "Click 'Open Folder' to view items inside the Windows Recycle Bin.",
                    "Geri Dönüşüm Kutusu içeriğini görmek için 'Klasörü Aç' butonuna tıklayın.",
                    "点击下方“在资源管理器中打开”可查看回收站内的具体文件。"));
                lvi.ForeColor = ColMuted;
                _lvFiles.Items.Add(lvi);
            }
            else if (cat.TopFiles.Count == 0)
            {
                ListViewItem lvi = new ListViewItem(LangHelper.T(lang, "No eligible files found.", "Temizlenecek dosya bulunamadı.", "未发现可清理文件。"));
                lvi.ForeColor = ColMuted;
                _lvFiles.Items.Add(lvi);
            }
            else
            {
                foreach (ScannedFileEntry f in cat.TopFiles)
                {
                    ListViewItem lvi = new ListViewItem(f.FileName);
                    string dt = (lang == "tr")
                        ? f.LastWriteTime.ToString("dd.MM.yyyy HH:mm", c)
                        : f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", c);
                    lvi.SubItems.Add(dt);
                    lvi.SubItems.Add(CleanupEngine.FormatBytes(f.SizeBytes, lang));
                    lvi.Tag = f.FullPath;
                    _lvFiles.Items.Add(lvi);
                }
            }
            _lvFiles.DoubleClick += delegate { OpenSelectedFileLocation(); };
            Controls.Add(_lvFiles);

            Button btnOpen = new Button();
            btnOpen.UseMnemonic = false;
            btnOpen.Text = LangHelper.T(lang, "Open Folder in Explorer", "Klasörü Gezginde Aç", "在资源管理器中打开");
            btnOpen.FlatStyle = FlatStyle.Flat;
            btnOpen.BackColor = ColSurface;
            btnOpen.ForeColor = ColSafe;
            btnOpen.FlatAppearance.BorderColor = ColBorder;
            btnOpen.Location = new Point(20, 448);
            btnOpen.Size = new Size(192, 34);
            btnOpen.Cursor = Cursors.Hand;
            btnOpen.Click += delegate { OpenSelectedFolder(); };
            Controls.Add(btnOpen);

            Button btnClose = new Button();
            btnClose.UseMnemonic = false;
            btnClose.Text = LangHelper.T(lang, "Close", "Kapat", "关闭");
            btnClose.FlatStyle = FlatStyle.Flat;
            btnClose.BackColor = ColSurface;
            btnClose.ForeColor = ColText;
            btnClose.FlatAppearance.BorderColor = ColBorder;
            btnClose.Location = new Point(536, 448);
            btnClose.Size = new Size(94, 34);
            btnClose.Cursor = Cursors.Hand;
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);

            CancelButton = btnClose;
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeWindowStyle.ApplyDarkTitleBar(Handle, ColBg, ColText);
        }

        internal static ListView CreateThemedDarkListView(Point loc, Size size, Color surfaceColor, Color bodyColor, string fontName)
        {
            ListView lv = new ListView();
            lv.Location = loc;
            lv.Size = size;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = false;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = surfaceColor;
            lv.ForeColor = bodyColor;
            Font lvFont = new Font(fontName, 8.6f);
            lv.Font = lvFont;
            lv.Disposed += delegate { try { lvFont.Dispose(); } catch { } };
            lv.OwnerDraw = true;

            Color hdrBg = Color.FromArgb(28, 35, 46);
            Color hdrBorder = Color.FromArgb(46, 56, 70);
            Color hdrText = Color.FromArgb(148, 163, 184);
            Color selBg = Color.FromArgb(30, 48, 62);

            lv.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush bg = new SolidBrush(hdrBg))
                using (Pen bd = new Pen(hdrBorder, 1f))
                using (SolidBrush fg = new SolidBrush(hdrText))
                using (Font f = new Font(fontName, 8.2f, FontStyle.Bold))
                using (StringFormat sf = new StringFormat())
                {
                    e.Graphics.FillRectangle(bg, e.Bounds);
                    e.Graphics.DrawLine(bd, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Alignment = e.Header.TextAlign == HorizontalAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    Rectangle textRc = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 16), e.Bounds.Height);
                    e.Graphics.DrawString(e.Header.Text, f, fg, textRc, sf);
                }
            };

            lv.DrawItem += delegate(object sender, DrawListViewItemEventArgs e)
            {
            };

            lv.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                Rectangle cellRc = e.ColumnIndex == 0
                    ? new Rectangle(e.Bounds.X, e.Bounds.Y, e.Header.Width, e.Bounds.Height)
                    : e.Bounds;
                Color rowBg = e.Item.Selected ? selBg : surfaceColor;
                Color fgCol = e.Item.ForeColor != Color.Empty ? e.Item.ForeColor : bodyColor;
                using (SolidBrush bg = new SolidBrush(rowBg))
                using (SolidBrush fg = new SolidBrush(fgCol))
                using (StringFormat sf = new StringFormat())
                {
                    e.Graphics.FillRectangle(bg, cellRc);
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Alignment = e.Header.TextAlign == HorizontalAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    Rectangle textRc = new Rectangle(cellRc.X + 8, cellRc.Y, Math.Max(1, cellRc.Width - 16), cellRc.Height);
                    e.Graphics.DrawString(e.SubItem.Text, lv.Font, fg, textRc, sf);
                }
            };

            return lv;
        }

        private void OpenSelectedFolder()
        {
            try
            {
                if (_cat.IsRecycleBin) { Process.Start("explorer.exe", "shell:RecycleBinFolder"); return; }
                string pathToOpen = null;
                if (_lvFolders.SelectedItems.Count > 0) pathToOpen = _lvFolders.SelectedItems[0].Tag as string;
                else if (_cat.FolderBreakdowns.Count > 0) pathToOpen = _cat.FolderBreakdowns[0].Path;
                if (!string.IsNullOrEmpty(pathToOpen) && Directory.Exists(pathToOpen))
                    Process.Start("explorer.exe", "\"" + pathToOpen + "\"");
            }
            catch { }
        }

        private void OpenSelectedFileLocation()
        {
            try
            {
                if (_lvFiles.SelectedItems.Count > 0)
                {
                    string filePath = _lvFiles.SelectedItems[0].Tag as string;
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        Process.Start("explorer.exe", "/select,\"" + filePath + "\"");
                }
            }
            catch { }
        }
    }

    internal sealed class ConfirmCleanDialog : Form
    {
        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColSurface = Color.FromArgb(22, 27, 34);
        private static readonly Color ColBorder = Color.FromArgb(48, 58, 72);
        private static readonly Color ColText = Color.FromArgb(240, 246, 252);
        private static readonly Color ColBody = Color.FromArgb(204, 212, 220);
        private static readonly Color ColMuted = Color.FromArgb(139, 148, 158);
        private static readonly Color ColSafe = Color.FromArgb(52, 211, 153);
        private static readonly Color ColCaution = Color.FromArgb(251, 191, 36);
        private readonly List<Font> _ownedFonts = new List<Font>();

        private Font TrackFont(Font f) { _ownedFonts.Add(f); return f; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Font f in _ownedFonts) { try { f.Dispose(); } catch { } }
                _ownedFonts.Clear();
            }
            base.Dispose(disposing);
        }

        public ConfirmCleanDialog(List<CleanupCategory> selected, string lang)
        {
            CultureInfo c = LangHelper.GetCulture(lang);
            string fontName = LangHelper.UiFontFamily(lang);
            long totalBytes = 0, totalFiles = 0;
            bool hasCaution = false, hasRunning = false;

            foreach (CleanupCategory cat in selected)
            {
                totalBytes += cat.TotalBytes;
                totalFiles += cat.FileCount;
                if (cat.Tier == SafetyTier.Caution) hasCaution = true;
                if (cat.IsAppRunning) hasRunning = true;
            }

            Text = LangHelper.T(lang, "Confirm Cleanup", "Temizliği Onayla", "确认清理");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = TrackFont(new Font(fontName, 9f, FontStyle.Regular));

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(470, 390);

            Label lblTitle = new Label();
            lblTitle.UseMnemonic = false;
            lblTitle.Text = string.Format(
                c,
                LangHelper.T(lang, "Permanently clean {0} ({1:N0} files)", "{0} ({1:N0} dosya) kalıcı olarak temizlenecek", "即将永久清理 {0} (共 {1:N0} 个文件)"),
                CleanupEngine.FormatBytes(totalBytes, lang),
                totalFiles);
            lblTitle.Font = TrackFont(new Font(fontName, 10.8f, FontStyle.Bold));
            lblTitle.ForeColor = hasCaution ? ColCaution : ColSafe;
            lblTitle.Location = new Point(20, 16);
            lblTitle.Size = new Size(430, 24);
            Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.UseMnemonic = false;
            lblSub.Text = LangHelper.T(
                lang,
                "Selected items are listed below. Locked/in-use files and symlinks are automatically protected.",
                "Seçtiğiniz kalemler aşağıdadır. Aktif/kilitli dosyalar ve sembolik bağlantılar otomatik korunur.",
                "已选清理项目如下。正在使用的锁定文件与符号链接将被自动保护并跳过。");
            lblSub.ForeColor = ColMuted;
            lblSub.Location = new Point(20, 44);
            lblSub.Size = new Size(430, 34);
            Controls.Add(lblSub);

            ListView lv = FolderInspectorDialog.CreateThemedDarkListView(new Point(20, 84), new Size(430, 186), ColSurface, ColBody, fontName);
            lv.Columns.Add(LangHelper.T(lang, "Category", "Kategori", "清理项目"), 268);
            lv.Columns.Add(LangHelper.T(lang, "Files", "Dosya", "文件数"), 66, HorizontalAlignment.Right);
            lv.Columns.Add(LangHelper.T(lang, "Size", "Boyut", "大小"), 94, HorizontalAlignment.Right);

            foreach (CleanupCategory cat in selected)
            {
                string prefix = cat.Tier == SafetyTier.Caution ? "[!] " : "";
                ListViewItem lvi = new ListViewItem(prefix + cat.GetName(lang));
                lvi.SubItems.Add(cat.FileCount.ToString("N0", c));
                lvi.SubItems.Add(CleanupEngine.FormatBytes(cat.TotalBytes, lang));
                if (cat.Tier == SafetyTier.Caution) lvi.ForeColor = ColCaution;
                lv.Items.Add(lvi);
            }
            Controls.Add(lv);

            string warnText = "";
            if (hasCaution)
                warnText += LangHelper.T(lang, "• Caution [!] items are selected (e.g. Recycle Bin / Shader cache).\r\n", "• Dikkat [!] işaretli öğeler seçili (örn. Geri Dönüşüm Kutusu / Shader önbelleği).\r\n", "• 已勾选谨慎 [!] 项目（如回收站 / 显卡着色器缓存）。\r\n");
            if (hasRunning)
                warnText += LangHelper.T(lang, "• Some open browser/app cache files are locked and will be safely skipped.", "• Açık olan tarayıcı/uygulamaların kilitli önbellek dosyaları güvenle atlanacaktır.", "• 部分正在运行的浏览器/应用缓存文件已锁定，将自动安全跳过。");
            if (string.IsNullOrEmpty(warnText))
                warnText = LangHelper.T(
                    lang,
                    "Only SAFE caches & temporary files are selected. Personal files, logins & cookies are untouched.",
                    "Yalnızca güvenli (SAFE) önbellek ve geçici dosyalar seçildi. Kişisel dosyalarınız ve şifreleriniz etkilenmez.",
                    "仅勾选了安全缓存与临时文件，您的个人文档、密码与登录状态完全不受影响。");

            Label lblWarn = new Label();
            lblWarn.UseMnemonic = false;
            lblWarn.Text = warnText.Trim();
            lblWarn.ForeColor = (hasCaution || hasRunning) ? ColCaution : ColMuted;
            lblWarn.Font = TrackFont(new Font(fontName, 8.3f));
            lblWarn.Location = new Point(20, 280);
            lblWarn.Size = new Size(430, 48);
            Controls.Add(lblWarn);

            Button btnConfirm = new Button();
            btnConfirm.UseMnemonic = false;
            btnConfirm.Text = LangHelper.T(lang, "Confirm & Clean", "Onayla ve Temizle", "确认并清理");
            btnConfirm.FlatStyle = FlatStyle.Flat;
            btnConfirm.BackColor = Color.FromArgb(18, 66, 48);
            btnConfirm.ForeColor = Color.FromArgb(110, 231, 183);
            btnConfirm.FlatAppearance.BorderColor = ColSafe;
            btnConfirm.Location = new Point(216, 338);
            btnConfirm.Size = new Size(134, 34);
            btnConfirm.Font = TrackFont(new Font(fontName, 9f, FontStyle.Bold));
            btnConfirm.Cursor = Cursors.Hand;
            btnConfirm.DialogResult = DialogResult.OK;
            Controls.Add(btnConfirm);

            Button btnCancel = new Button();
            btnCancel.UseMnemonic = false;
            btnCancel.Text = LangHelper.T(lang, "Cancel", "İptal", "取消");
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.BackColor = ColSurface;
            btnCancel.ForeColor = ColText;
            btnCancel.FlatAppearance.BorderColor = ColBorder;
            btnCancel.Location = new Point(358, 338);
            btnCancel.Size = new Size(92, 34);
            btnCancel.Cursor = Cursors.Hand;
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnConfirm;
            CancelButton = btnCancel;
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeWindowStyle.ApplyDarkTitleBar(Handle, ColBg, ColText);
        }
    }
}
