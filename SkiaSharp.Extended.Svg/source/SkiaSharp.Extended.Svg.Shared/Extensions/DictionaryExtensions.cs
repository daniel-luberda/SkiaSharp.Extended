using System;
using System.Collections.Generic;

namespace SkiaSharp.Extended.Svg
{
	internal static class DictionaryExtensions
	{
		public static string GetString(this Dictionary<string, string> style, string name, string defaultValue = "")
		{
			if (style.TryGetValue(name, out string v))
				return v;
			return defaultValue;
		}

		public static SKFontStyleSlant ReadFontStyle(this Dictionary<string, string> fontStyle, SKFontStyleSlant defaultStyle = SKFontStyleSlant.Upright)
		{
			SKFontStyleSlant style = defaultStyle;

			if (fontStyle.TryGetValue("font-style", out string fstyle) && !string.IsNullOrWhiteSpace(fstyle))
			{
				switch (fstyle)
				{
					case "italic":
						style = SKFontStyleSlant.Italic;
						break;
					case "oblique":
						style = SKFontStyleSlant.Oblique;
						break;
					case "normal":
						style = SKFontStyleSlant.Upright;
						break;
					default:
						style = defaultStyle;
						break;
				}
			}

			return style;
		}

		public static int ReadFontWidth(this Dictionary<string, string> fontStyle, int defaultWidth = (int)SKFontStyleWidth.Normal)
		{
			var width = defaultWidth;
			if (fontStyle.TryGetValue("font-stretch", out string fwidth) && !string.IsNullOrWhiteSpace(fwidth) && !int.TryParse(fwidth, out width))
			{
				switch (fwidth)
				{
					case "ultra-condensed":
						width = (int)SKFontStyleWidth.UltraCondensed;
						break;
					case "extra-condensed":
						width = (int)SKFontStyleWidth.ExtraCondensed;
						break;
					case "condensed":
						width = (int)SKFontStyleWidth.Condensed;
						break;
					case "semi-condensed":
						width = (int)SKFontStyleWidth.SemiCondensed;
						break;
					case "normal":
						width = (int)SKFontStyleWidth.Normal;
						break;
					case "semi-expanded":
						width = (int)SKFontStyleWidth.SemiExpanded;
						break;
					case "expanded":
						width = (int)SKFontStyleWidth.Expanded;
						break;
					case "extra-expanded":
						width = (int)SKFontStyleWidth.ExtraExpanded;
						break;
					case "ultra-expanded":
						width = (int)SKFontStyleWidth.UltraExpanded;
						break;
					case "wider":
						width = width + 1;
						break;
					case "narrower":
						width = width - 1;
						break;
					default:
						width = defaultWidth;
						break;
				}
			}

			return Math.Min(Math.Max((int)SKFontStyleWidth.UltraCondensed, width), (int)SKFontStyleWidth.UltraExpanded);
		}

		public static int ReadFontWeight(this Dictionary<string, string> fontStyle, int defaultWeight = (int)SKFontStyleWeight.Normal)
		{
			var weight = defaultWeight;

			if (fontStyle.TryGetValue("font-weight", out string fweight) && !string.IsNullOrWhiteSpace(fweight) && !int.TryParse(fweight, out weight))
			{
				switch (fweight)
				{
					case "normal":
						weight = (int)SKFontStyleWeight.Normal;
						break;
					case "bold":
						weight = (int)SKFontStyleWeight.Bold;
						break;
					case "bolder":
						weight = weight + 100;
						break;
					case "lighter":
						weight = weight - 100;
						break;
					default:
						weight = defaultWeight;
						break;
				}
			}

			return Math.Min(Math.Max((int)SKFontStyleWeight.Thin, weight), (int)SKFontStyleWeight.ExtraBlack);
		}
	}
}
