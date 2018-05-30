using System;
using System.Xml.Linq;

namespace SkiaSharp.Extended.Svg
{
	internal static class XNameExtensions
	{
		public static bool HasSvgNamespace(this XName name)
		{
			return
				string.IsNullOrEmpty(name.Namespace?.NamespaceName) ||
				name.Namespace == SKSvg.SvgNamespace ||
				name.Namespace == SKSvg.XLinkNamespace;
		}
	}
}
