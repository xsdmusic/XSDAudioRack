using System;
using System.Drawing;
using System.Windows.Forms;

namespace XSDAudioRack.Vst3;

/// <summary>
/// 无自定义界面插件（Airwindows 系等 createView 返回空的插件）的通用参数编辑器：
/// 控制器导出的每个参数一条滑杆（或开关），通过 setParamNormalized 直接调参。
/// </summary>
internal sealed class GenericParameterEditor : Form
{
    private const int ParamFlags_IsHidden = 1 << 4;   // ParameterInfo::kIsHidden
    private const int ParamFlags_IsBypass = 1 << 16;   // ParameterInfo::kIsBypass

    public GenericParameterEditor(Vst3Plugin plugin)
    {
        Text = plugin.Name + " — 参数";
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 32);
        ForeColor = Color.Gainsboro;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScroll = true;

        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 12),
        };

        int shown = 0;
        int count = plugin.ParameterCount;
        for (int i = 0; i < count; i++)
        {
            var info = plugin.ParameterInfo(i);
            if (info == null) continue;
            var (id, title, units, stepCount, _, flags) = info.Value;
            if ((flags & ParamFlags_IsHidden) != 0) continue;   // 隐藏参数不显示

            var row = BuildRow(plugin, id, title, units, stepCount);
            if (row != null) { list.Controls.Add(row); shown++; }
        }

        if (shown == 0)
        {
            list.Controls.Add(new Label
            {
                Text = "（该插件没有可显示的参数）",
                AutoSize = true,
                ForeColor = Color.Gray,
            });
        }

        Controls.Add(list);
        ClientSize = new Size(500, Math.Min(680, shown * 46 + 40));
    }

    private Control BuildRow(Vst3Plugin plugin, uint id, string title, string units, int stepCount)
    {
        double current = plugin.GetParam(id);
        bool isToggle = stepCount == 1;
        string display = string.IsNullOrEmpty(title) ? $"参数 {id}" : title;

        var panel = new Panel { Width = 452, Height = 44, BackColor = Color.FromArgb(30, 30, 40) };

        var name = new Label
        {
            Text = display,
            Location = new Point(8, 13),
            AutoSize = false,
            Width = 150,
            ForeColor = Color.Gainsboro,
        };
        panel.Controls.Add(name);

        if (isToggle)
        {
            var cb = new CheckBox
            {
                Text = "",
                Checked = current >= 0.5,
                Location = new Point(170, 12),
                AutoSize = true,
            };
            cb.CheckedChanged += (_, _) => plugin.SetParam(id, cb.Checked ? 1.0 : 0.0);
            panel.Controls.Add(cb);
        }
        else
        {
            var tb = new TrackBar
            {
                Minimum = 0,
                Maximum = stepCount > 0 ? stepCount : 1000,
                Width = 210,
                Location = new Point(162, 10),
                TickFrequency = stepCount > 0 ? 1 : 100,
            };
            var val = new Label
            {
                Location = new Point(378, 13),
                AutoSize = false,
                Width = 68,
                ForeColor = Color.FromArgb(140, 200, 255),
            };

            string Fmt(double v)
            {
                var s = stepCount > 0
                    ? Math.Round(v * stepCount).ToString()
                    : v.ToString("0.000");
                return string.IsNullOrEmpty(units) ? s : s + " " + units;
            }

            tb.Value = Math.Max(0, Math.Min(tb.Maximum, (int)Math.Round(current * tb.Maximum)));
            val.Text = Fmt(current);

            tb.ValueChanged += (_, _) =>
            {
                double v = (double)tb.Value / tb.Maximum;
                plugin.SetParam(id, v);
                val.Text = Fmt(v);
            };

            panel.Controls.Add(tb);
            panel.Controls.Add(val);
        }
        return panel;
    }
}
