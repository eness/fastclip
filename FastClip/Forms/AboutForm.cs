using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace FastClip.Forms;

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About FastClip";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(820, 520);
        BackColor = Color.FromArgb(244, 247, 245);

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 260,
            BackColor = Color.FromArgb(228, 236, 230)
        };

        var bannerBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(228, 236, 230),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Image = LoadAboutBanner()
        };

        headerPanel.Controls.Add(bannerBox);

        var nameLabel = new Label
        {
            AutoSize = false,
            Location = new Point(72, 286),
            Size = new Size(676, 30),
            Text = "Enes Sönmez",
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(32, 32, 32),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var descriptionLabel = new Label
        {
            AutoSize = false,
            Location = new Point(108, 316),
            Size = new Size(604, 52),
            Text = "Clipboard-to-file workflow utility for fast image replacement and export on Windows.",
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(90, 90, 90),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var websiteLink = BuildLinkLabel("enes.dev", "https://enes.dev", new Point(186, 388), new Size(84, 28));
        var separatorLabel1 = BuildSeparatorLabel(new Point(272, 388));
        var xLink = BuildLinkLabel("x.com/enes_dev", "https://x.com/enes_dev", new Point(294, 388), new Size(118, 28));
        var separatorLabel2 = BuildSeparatorLabel(new Point(414, 388));
        var githubLink = BuildLinkLabel("github.com/eness/fastclip", "https://github.com/eness/fastclip", new Point(436, 388), new Size(196, 28));

        var closeButton = new Button
        {
            Text = "Close",
            Size = new Size(94, 34),
            Location = new Point((820 - 94) / 2, 452),
            BackColor = Color.White
        };
        closeButton.Click += (_, _) => Close();

        Controls.Add(headerPanel);
        Controls.Add(nameLabel);
        Controls.Add(descriptionLabel);
        Controls.Add(websiteLink);
        Controls.Add(separatorLabel1);
        Controls.Add(xLink);
        Controls.Add(separatorLabel2);
        Controls.Add(githubLink);
        Controls.Add(closeButton);
    }

    private static Label BuildSeparatorLabel(Point location)
    {
        return new Label
        {
            AutoSize = false,
            Location = location,
            Size = new Size(20, 28),
            Text = "•",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(125, 125, 125)
        };
    }

    private static Image? LoadAboutBanner()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "banner.jpg"),
            Path.Combine(AppContext.BaseDirectory, "..", "banner.jpg"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "banner.jpg")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sourceImage = Image.FromStream(stream);
                    return new Bitmap(sourceImage);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static LinkLabel BuildLinkLabel(string text, string url, Point location, Size size)
    {
        var link = new LinkLabel
        {
            AutoSize = false,
            Location = location,
            Size = size,
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point),
            LinkColor = Color.FromArgb(40, 98, 72),
            ActiveLinkColor = Color.FromArgb(24, 72, 50),
            VisitedLinkColor = Color.FromArgb(40, 98, 72)
        };

        link.Click += (_, _) => OpenExternalUrl(url);
        return link;
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
