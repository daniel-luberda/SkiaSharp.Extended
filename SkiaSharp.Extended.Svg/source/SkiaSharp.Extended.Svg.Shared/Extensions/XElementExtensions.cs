using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace SkiaSharp.Extended.Svg
{
	internal static class XElementExtensions
	{
		public static Dictionary<string, string> ReadStyle(this XElement e)
		{
			// get from local attributes
			var dic = e.Attributes().Where(a => a.Name.HasSvgNamespace()).ToDictionary(k => k.Name.LocalName, v => v.Value);

			var style = e.Attribute("style")?.Value;
			if (!string.IsNullOrWhiteSpace(style))
			{
				// get from stlye attribute
				var styleDic = style.ReadStyle();

				// overwrite
				foreach (var pair in styleDic)
					dic[pair.Key] = pair.Value;
			}

			return dic;
		}

		public static SKShaderTileMode ReadSpreadMethod(this XElement e)
		{
			var repeat = e.Attribute("spreadMethod")?.Value;
			switch (repeat)
			{
				case "reflect":
					return SKShaderTileMode.Mirror;
				case "repeat":
					return SKShaderTileMode.Repeat;
				case "pad":
				default:
					return SKShaderTileMode.Clamp;
			}
		}

		public static string ReadHrefString(this XElement e)
		{
			return (e.Attribute("href") ?? e.Attribute(SKSvg.XLinkNamespace + "href"))?.Value;
		}

		public static SKTextAlign ReadTextAlignment(this XElement element)
		{
			string value = null;
			if (element != null)
			{
				var attrib = element.Attribute("text-anchor");
				if (attrib != null && !string.IsNullOrWhiteSpace(attrib.Value))
					value = attrib.Value;
				else
				{
					var style = element.Attribute("style");
					if (style != null && !string.IsNullOrWhiteSpace(style.Value))
					{
						value = style.Value.ReadStyle().GetString("text-anchor");
					}
				}
			}

			switch (value)
			{
				case "end":
					return SKTextAlign.Right;
				case "middle":
					return SKTextAlign.Center;
				default:
					return SKTextAlign.Left;
			}
		}
	}
}
