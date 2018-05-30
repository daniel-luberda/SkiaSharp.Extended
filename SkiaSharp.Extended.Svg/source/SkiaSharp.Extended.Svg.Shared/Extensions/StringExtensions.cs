using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SkiaSharp.Extended.Svg
{
	internal static class StringExtensions
	{
		private static readonly Regex keyValueRe = new Regex(@"\s*([\w-]+)\s*:\s*(.*)");
		private static readonly Regex unitRe = new Regex("px|pt|em|ex|pc|cm|mm|in");

		public static Dictionary<string, string> ReadStyle(this string style)
		{
			var d = new Dictionary<string, string>();
			var kvs = style.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (var kv in kvs)
			{
				var m = keyValueRe.Match(kv);
				if (m.Success)
				{
					var k = m.Groups[1].Value;
					var v = m.Groups[2].Value;
					d[k] = v;
				}
			}
			return d;
		}

		public static byte[] ReadUriBytes(this string uri)
		{
			if (!string.IsNullOrEmpty(uri))
			{
				var offset = uri.IndexOf(",");
				if (offset != -1 && offset - 1 < uri.Length)
				{
					uri = uri.Substring(offset + 1);
					return Convert.FromBase64String(uri);
				}
			}

			return null;
		}
	}
}
