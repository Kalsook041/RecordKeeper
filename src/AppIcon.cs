using System;
using System.Drawing;
using System.IO;

namespace ReplayKeeper
{
    internal static class AppIcon
    {
        public static Icon Load(Size size)
        {
            using (Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ReplayKeeper.AppIcon"))
            {
                if (stream == null) throw new InvalidOperationException("程序图标资源缺失，请重新构建。");
                using (Icon icon = new Icon(stream, size))
                    return (Icon)icon.Clone();
            }
        }
    }
}
