using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SkiaSharp.Extended.Svg
{
	public class SKSvg
	{
		private const float DefaultPPI = 160f;
		private const bool DefaultThrowOnUnsupportedElement = false;

		internal static readonly XNamespace XLinkNamespace = "http://www.w3.org/1999/xlink";
		internal static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";

		private static readonly IFormatProvider icult = CultureInfo.InvariantCulture;
		private static readonly char[] WS = new char[] { ' ', '\t', '\n', '\r' };
		private static readonly Regex unitRe = new Regex("px|pt|em|ex|pc|cm|mm|in");
		private static readonly Regex percRe = new Regex("%");
		private static readonly Regex fillUrlRe = new Regex(@"url\s*\(\s*#([^\)]+)\)");
		private static readonly Regex clipPathUrlRe = new Regex(@"url\s*\(\s*#([^\)]+)\)");
		private static readonly Regex keyValueRe = new Regex(@"\s*([\w-]+)\s*:\s*(.*)");
		private static readonly Regex WSRe = new Regex(@"\s{2,}");

		private readonly Dictionary<string, XElement> defs = new Dictionary<string, XElement>();
		private readonly Dictionary<string, object> fills = new Dictionary<string, object>();
		private readonly Dictionary<string, string> styles = new Dictionary<string, string>();
		private readonly XmlReaderSettings xmlReaderSettings = new XmlReaderSettings()
		{
			DtdProcessing = DtdProcessing.Ignore,
			IgnoreComments = true,
		};

#if PORTABLE
		// basically use reflection to try and find a method that supports a 
		// file path AND a XmlParserContext...
		private static readonly MethodInfo createReaderMethod;

		static SKSvg()
		{
			// try and find `Create(string, XmlReaderSettings, XmlParserContext)`
			createReaderMethod = typeof(XmlReader).GetRuntimeMethod(
				nameof(XmlReader.Create),
				new[] { typeof(string), typeof(XmlReaderSettings), typeof(XmlParserContext) });
		}
#endif

		public SKSvg()
			: this(DefaultPPI, SKSize.Empty)
		{
		}

		public SKSvg(float pixelsPerInch)
			: this(pixelsPerInch, SKSize.Empty)
		{
		}

		public SKSvg(SKSize canvasSize)
			: this(DefaultPPI, canvasSize)
		{
		}

		public SKSvg(float pixelsPerInch, SKSize canvasSize)
		{
			CanvasSize = canvasSize;
			PixelsPerInch = pixelsPerInch;
			ThrowOnUnsupportedElement = DefaultThrowOnUnsupportedElement;
		}

		public float PixelsPerInch { get; set; }

		public bool ThrowOnUnsupportedElement { get; set; }

		public SKRect ViewBox { get; private set; }

		public SKSize CanvasSize { get; private set; }

		public SKPicture Picture { get; private set; }

		public string Description { get; private set; }

		public string Title { get; private set; }

		public string Version { get; private set; }

		public SKPicture Load(string filename)
		{
#if PORTABLE
			// PCL does not have the ability to read a file and use a context
			if (createReaderMethod == null)
			{
				return Load(XDocument.Load(filename));
			}

			// we know that there we can access the method via reflection
			var args = new object[] { filename, xmlReaderSettings, CreateSvgXmlContext() };
			using (var reader = (XmlReader)createReaderMethod.Invoke(null, args))
			{
				return Load(reader);
			}
#else
			using (var stream = File.OpenRead(filename))
			{
				return Load(stream);
			}
#endif
		}

		public SKPicture Load(Stream stream)
		{
			using (var reader = XmlReader.Create(stream, xmlReaderSettings, CreateSvgXmlContext()))
			{
				return Load(reader);
			}
		}

		public SKPicture Load(XmlReader reader)
		{
			return Load(XDocument.Load(reader));
		}

		private static XmlParserContext CreateSvgXmlContext()
		{
			var table = new NameTable();
			var manager = new XmlNamespaceManager(table);
			manager.AddNamespace(string.Empty, SvgNamespace.NamespaceName);
			manager.AddNamespace("xlink", XLinkNamespace.NamespaceName);
			return new XmlParserContext(null, manager, null, XmlSpace.None);
		}

		private SKPicture Load(XDocument xdoc)
		{
			var svg = xdoc.Root;
			var ns = svg.Name.Namespace;

			// find the defs (gradients) - and follow all hrefs
			foreach (var d in svg.Descendants())
			{
				var id = d.Attribute("id")?.Value?.Trim();
				if (!string.IsNullOrEmpty(id))
					defs[id] = ReadDefinition(d);
			}

			Version = svg.Attribute("version")?.Value;
			Title = svg.Element(ns + "title")?.Value;
			Description = svg.Element(ns + "desc")?.Value ?? svg.Element(ns + "description")?.Value;

			// TODO: parse the "preserveAspectRatio" values properly
			var preserveAspectRatio = svg.Attribute("preserveAspectRatio")?.Value;

			// get the SVG dimensions
			var viewBoxA = svg.Attribute("viewBox") ?? svg.Attribute("viewPort");
			if (viewBoxA != null)
			{
				ViewBox = ReadRectangle(viewBoxA.Value);
			}

			if (CanvasSize.IsEmpty)
			{
				// get the user dimensions
				var widthA = svg.Attribute("width");
				var heightA = svg.Attribute("height");
				var width = ReadNumber(widthA);
				var height = ReadNumber(heightA);
				var size = new SKSize(width, height);

				if (widthA == null)
				{
					size.Width = ViewBox.Width;
				}
				else if (widthA.Value.Contains("%"))
				{
					size.Width *= ViewBox.Width;
				}
				if (heightA == null)
				{
					size.Height = ViewBox.Height;
				}
				else if (heightA != null && heightA.Value.Contains("%"))
				{
					size.Height *= ViewBox.Height;
				}

				// set the property
				CanvasSize = size;
			}

			// create the picture from the elements
			using (var recorder = new SKPictureRecorder())
			using (var canvas = recorder.BeginRecording(SKRect.Create(CanvasSize)))
			{
				// if there is no viewbox, then we don't do anything, otherwise
				// scale the SVG dimensions to fit inside the user dimensions
				if (!ViewBox.IsEmpty && (ViewBox.Width != CanvasSize.Width || ViewBox.Height != CanvasSize.Height))
				{
					if (preserveAspectRatio == "none")
					{
						canvas.Scale(CanvasSize.Width / ViewBox.Width, CanvasSize.Height / ViewBox.Height);
					}
					else
					{
						// TODO: just center scale for now
						var scale = Math.Min(CanvasSize.Width / ViewBox.Width, CanvasSize.Height / ViewBox.Height);
						var centered = SKRect.Create(CanvasSize).AspectFit(ViewBox.Size);
						canvas.Translate(centered.Left, centered.Top);
						canvas.Scale(scale, scale);
					}
				}

				// translate the canvas by the viewBox origin
				canvas.Translate(-ViewBox.Left, -ViewBox.Top);

				// if the viewbox was specified, then crop to that
				if (!ViewBox.IsEmpty)
				{
					canvas.ClipRect(ViewBox);
				}

				// read style
				SKPaint stroke = null;
				SKPaint fill = CreatePaint();
				var style = ReadPaints(svg, ref stroke, ref fill, true);

				// read elements
				LoadElements(svg.Elements(), canvas, stroke, fill);

				Picture = recorder.EndRecording();
			}

			return Picture;
		}

		private void LoadElements(IEnumerable<XElement> elements, SKCanvas canvas, SKPaint stroke, SKPaint fill)
		{
			foreach (var e in elements)
			{
				ReadElement(e, canvas, stroke, fill);
			}
		}

		private void ReadElement(XElement e, SKCanvas canvas, SKPaint stroke, SKPaint fill)
		{
			if (e.Attribute("display")?.Value == "none")
				return;

			// transform matrix
			var transform = ReadTransform(e.Attribute("transform")?.Value ?? string.Empty);
			canvas.Save();
			canvas.Concat(ref transform);

			// clip-path
			var clipPath = ReadClipPath(e.Attribute("clip-path")?.Value ?? string.Empty);
			if (clipPath != null)
			{
				canvas.ClipPath(clipPath);
			}

			// SVG element
			var elementName = e.Name.LocalName;
			var isGroup = elementName == "g";

			// read style
			var style = ReadPaints(e, ref stroke, ref fill, isGroup);

			// parse elements
			switch (elementName)
			{
				case "image":
					{
						var image = ReadImage(e);
						if (image.Bytes != null)
						{
							using (var bitmap = SKBitmap.Decode(image.Bytes))
							{
								if (bitmap != null)
								{
									canvas.DrawBitmap(bitmap, image.Rect);
								}
							}
						}
						break;
					}
				case "text":
					if (stroke != null || fill != null)
					{
						var spans = ReadText(e, stroke?.Clone(), fill?.Clone());
						if (spans.Any())
						{
							canvas.DrawText(spans);
						}
					}
					break;
				case "rect":
				case "ellipse":
				case "circle":
				case "path":
				case "polygon":
				case "polyline":
				case "line":
					{
						var elementPath = ReadElement(e);
						if (elementPath == null)
							break;

						string fillId = e.Attribute("fill")?.Value;
						if (!string.IsNullOrWhiteSpace(fillId) && fills.TryGetValue(fillId, out object addFill))
						{
							var x = ReadNumber(e.Attribute("x"));
							var y = ReadNumber(e.Attribute("y"));
							var elementSize = ReadElementSize(e);

							switch (addFill)
							{
								case SKLinearGradient gradient:
									var startPoint = gradient.GetStartPoint(x, y, elementSize.Width, elementSize.Height);
									var endPoint = gradient.GetEndPoint(x, y, elementSize.Width, elementSize.Height);

									using (var gradientShader = SKShader.CreateLinearGradient(startPoint, endPoint, gradient.Colors, gradient.Positions, gradient.TileMode))
									using (var gradientPaint = new SKPaint() { Shader = gradientShader, IsAntialias = true, BlendMode = SKBlendMode.SrcOver })
									{
										canvas.DrawPath(elementPath, gradientPaint);
									}
									break;
								case SKRadialGradient gradient:
									var centerPoint = gradient.GetCenterPoint(x, y, elementSize.Width, elementSize.Height);
									var radius = gradient.GetRadius(elementSize.Width, elementSize.Height);

									using (var gradientShader = SKShader.CreateRadialGradient(centerPoint, radius, gradient.Colors, gradient.Positions, gradient.TileMode))
									using (var gradientPaint = new SKPaint() { Shader = gradientShader, IsAntialias = true })
									{
										canvas.DrawPath(elementPath, gradientPaint);
									}
									break;
								default:
									if (fill != null)
										canvas.DrawPath(elementPath, fill);
									break;
							}
						}
						else if (fill != null)
						{
							canvas.DrawPath(elementPath, fill);
						}

						if (stroke != null)
							canvas.DrawPath(elementPath, stroke);

						break;
					}
				case "g":
					if (e.HasElements)
					{
						// get current group opacity
						float groupOpacity = ReadOpacity(style);
						if (groupOpacity != 1.0f)
						{
							var opacity = (byte)(255 * groupOpacity);
							var opacityPaint = new SKPaint { Color = SKColors.Black.WithAlpha(opacity) };

							// apply the opacity
							canvas.SaveLayer(opacityPaint);
						}

						foreach (var gElement in e.Elements())
						{
							ReadElement(gElement, canvas, stroke?.Clone(), fill?.Clone());
						}

						// restore state
						if (groupOpacity != 1.0f)
							canvas.Restore();
					}
					break;
				case "use":
					if (e.HasAttributes)
					{
						var href = ReadHref(e);
						if (href != null)
						{
							// create a deep copy as we will copy attributes
							href = new XElement(href);
							var attributes = e.Attributes();
							foreach (var attribute in attributes)
							{
								var name = attribute.Name.LocalName;

								if (!name.Contains("href")
									&& !name.Equals("id", StringComparison.OrdinalIgnoreCase)
									&& !name.Equals("transform", StringComparison.OrdinalIgnoreCase))
								{
									href.SetAttributeValue(attribute.Name, attribute.Value);
								}
							}

							ReadElement(href, canvas, stroke?.Clone(), fill?.Clone());
						}
					}
					break;
				case "switch":
					if (e.HasElements)
					{
						foreach (var ee in e.Elements())
						{
							var requiredFeatures = ee.Attribute("requiredFeatures");
							var requiredExtensions = ee.Attribute("requiredExtensions");
							var systemLanguage = ee.Attribute("systemLanguage");

							// TODO: evaluate requiredFeatures, requiredExtensions and systemLanguage
							var isVisible =
								requiredFeatures == null &&
								requiredExtensions == null &&
								systemLanguage == null;

							if (isVisible)
							{
								ReadElement(ee, canvas, stroke?.Clone(), fill?.Clone());
							}
						}
					}
					break;
				case "defs":
				case "title":
				case "desc":
				case "description":
					// already read earlier
					break;
				default:
					LogOrThrow($"SVG element '{elementName}' is not supported");
					break;
			}

			// restore matrix
			canvas.Restore();
		}

		private SKSvgImage ReadImage(XElement e)
		{
			var x = ReadNumber(e.Attribute("x"));
			var y = ReadNumber(e.Attribute("y"));
			var width = ReadNumber(e.Attribute("width"));
			var height = ReadNumber(e.Attribute("height"));
			var rect = SKRect.Create(x, y, width, height);

			byte[] bytes = null;

			var uri = e.ReadHrefString();
			if (uri != null)
			{
				if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
				{
					bytes = uri.ReadUriBytes();
				}
				else
				{
					LogOrThrow($"Remote images are not supported");
				}
			}

			return new SKSvgImage(rect, uri, bytes);
		}

		private SKPath ReadElement(XElement e)
		{
			var path = new SKPath();

			var elementName = e.Name.LocalName;
			switch (elementName)
			{
				case "rect":
					var rect = ReadRoundedRect(e);
					if (rect.IsRounded)
						path.AddRoundedRect(rect.Rect, rect.RadiusX, rect.RadiusY);
					else
						path.AddRect(rect.Rect);
					break;
				case "ellipse":
					var oval = ReadOval(e);
					path.AddOval(oval.BoundingRect);
					break;
				case "circle":
					var circle = ReadCircle(e);
					path.AddCircle(circle.Center.X, circle.Center.Y, circle.Radius);
					break;
				case "path":
					var d = e.Attribute("d")?.Value;
					if (!string.IsNullOrWhiteSpace(d))
					{
						path.Dispose();
						path = SKPath.ParseSvgPathData(d);
					}
					break;
				case "polygon":
				case "polyline":
					var close = elementName == "polygon";
					var p = e.Attribute("points")?.Value;
					if (!string.IsNullOrWhiteSpace(p))
					{
						p = "M" + p;
						if (close)
							p += " Z";
						path.Dispose();
						path = SKPath.ParseSvgPathData(p);
					}
					break;
				case "line":
					var line = ReadLine(e);
					path.MoveTo(line.P1);
					path.LineTo(line.P2);
					break;
				default:
					path.Dispose();
					path = null;
					break;
			}

			return path;
		}

		private SKOval ReadOval(XElement e)
		{
			var cx = ReadNumber(e.Attribute("cx"));
			var cy = ReadNumber(e.Attribute("cy"));
			var rx = ReadNumber(e.Attribute("rx"));
			var ry = ReadNumber(e.Attribute("ry"));

			return new SKOval(new SKPoint(cx, cy), rx, ry);
		}

		private SKCircle ReadCircle(XElement e)
		{
			var cx = ReadNumber(e.Attribute("cx"));
			var cy = ReadNumber(e.Attribute("cy"));
			var rr = ReadNumber(e.Attribute("r"));

			return new SKCircle(new SKPoint(cx, cy), rr);
		}

		private SKLine ReadLine(XElement e)
		{
			var x1 = ReadNumber(e.Attribute("x1"));
			var x2 = ReadNumber(e.Attribute("x2"));
			var y1 = ReadNumber(e.Attribute("y1"));
			var y2 = ReadNumber(e.Attribute("y2"));

			return new SKLine(new SKPoint(x1, y1), new SKPoint(x2, y2));
		}

		private SKRoundedRect ReadRoundedRect(XElement e)
		{
			var x = ReadNumber(e.Attribute("x"));
			var y = ReadNumber(e.Attribute("y"));
			var width = ReadNumber(e.Attribute("width"));
			var height = ReadNumber(e.Attribute("height"));
			var rx = ReadOptionalNumber(e.Attribute("rx"));
			var ry = ReadOptionalNumber(e.Attribute("ry"));
			var rect = SKRect.Create(x, y, width, height);

			return new SKRoundedRect(rect, rx ?? ry ?? 0, ry ?? rx ?? 0);
		}

		private SKText ReadText(XElement e, SKPaint stroke, SKPaint fill)
		{
			// TODO: stroke

			var x = ReadNumber(e.Attribute("x"));
			var y = ReadNumber(e.Attribute("y"));
			var xy = new SKPoint(x, y);
			var textAlign = e.ReadTextAlignment();
			var baselineShift = ReadBaselineShift(e);

			ReadFontAttributes(e, fill);

			return ReadTextSpans(e, xy, textAlign, baselineShift, stroke, fill);
		}

		private SKText ReadTextSpans(XElement e, SKPoint xy, SKTextAlign textAlign, float baselineShift, SKPaint stroke, SKPaint fill)
		{
			var spans = new SKText(xy, textAlign);

			// textAlign is used for all spans within the <text> element. If different textAligns would be needed, it is necessary to use
			// several <text> elements instead of <tspan> elements
			var currentBaselineShift = baselineShift;
			fill.TextAlign = SKTextAlign.Left;  // fixed alignment for all spans

			var nodes = e.Nodes().ToArray();
			for (int i = 0; i < nodes.Length; i++)
			{
				var c = nodes[i];
				bool isFirst = i == 0;
				bool isLast = i == nodes.Length - 1;

				if (c.NodeType == XmlNodeType.Text)
				{
					// TODO: check for preserve whitespace

					var textSegments = ((XText)c).Value.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
					var count = textSegments.Length;
					if (count > 0)
					{
						if (isFirst)
							textSegments[0] = textSegments[0].TrimStart();
						if (isLast)
							textSegments[count - 1] = textSegments[count - 1].TrimEnd();
						var text = WSRe.Replace(string.Concat(textSegments), " ");

						spans.Append(new SKTextSpan(text, fill.Clone(), baselineShift: currentBaselineShift));
					}
				}
				else if (c.NodeType == XmlNodeType.Element)
				{
					var ce = (XElement)c;
					if (ce.Name.LocalName == "tspan")
					{
						// the current span may want to change the cursor position
						var x = ReadOptionalNumber(ce.Attribute("x"));
						var y = ReadOptionalNumber(ce.Attribute("y"));
						var text = ce.Value; //.Trim();

						var spanFill = fill.Clone();
						ReadFontAttributes(ce, spanFill);

						// Don't read text-anchor from tspans!, Only use enclosing text-anchor from text element!
						currentBaselineShift = ReadBaselineShift(ce);

						spans.Append(new SKTextSpan(text, spanFill, x, y, currentBaselineShift));
					}
				}
			}

			return spans;
		}

		private void ReadFontAttributes(XElement e, SKPaint paint)
		{
			var fontStyle = e.ReadStyle();

			if (!fontStyle.TryGetValue("font-family", out string ffamily) || string.IsNullOrWhiteSpace(ffamily))
				ffamily = paint.Typeface?.FamilyName;
			var fweight = fontStyle.ReadFontWeight(paint.Typeface?.FontWeight ?? (int)SKFontStyleWeight.Normal);
			var fwidth = fontStyle.ReadFontWidth(paint.Typeface?.FontWidth ?? (int)SKFontStyleWidth.Normal);
			var fstyle = fontStyle.ReadFontStyle(paint.Typeface?.FontSlant ?? SKFontStyleSlant.Upright);

			paint.Typeface = SKTypeface.FromFamilyName(ffamily, fweight, fwidth, fstyle);

			if (fontStyle.TryGetValue("font-size", out string fsize) && !string.IsNullOrWhiteSpace(fsize))
				paint.TextSize = ReadNumber(fsize);
		}

		private SKSize ReadElementSize(XElement e)
		{
			float width = 0f;
            float height = 0f;
            var element = e;

			while (element.Parent != null)
            {
                if (!(width > 0f))
                    width = ReadNumber(element.Attribute("width"));

                if (!(height > 0f))
                    height = ReadNumber(element.Attribute("height"));

                if (width > 0f && height > 0f)
                    break;

                element = element.Parent;
            }

            if (!(width > 0f && height > 0f))
            {
                var root = e?.Document?.Root;
                width = ReadNumber(root?.Attribute("width"));
                height = ReadNumber(root?.Attribute("height"));
            }

			return new SKSize(width, height);
		}
        
		private Dictionary<string, string> ReadPaints(XElement e, ref SKPaint stroke, ref SKPaint fill, bool isGroup)
		{
			var style = e.ReadStyle();
			ReadPaints(style, ref stroke, ref fill, isGroup);
			return style;
		}

		private void ReadPaints(Dictionary<string, string> style, ref SKPaint strokePaint, ref SKPaint fillPaint, bool isGroup)
		{
			// get current element opacity, but ignore for groups (special case)
			float elementOpacity = isGroup ? 1.0f : ReadOpacity(style);

			// stroke
			var stroke = style.GetString("stroke").Trim();
			if (stroke.Equals("none", StringComparison.OrdinalIgnoreCase))
			{
				strokePaint = null;
			}
			else
			{
				if (string.IsNullOrEmpty(stroke))
				{
					// no change
				}
				else
				{
					if (strokePaint == null)
						strokePaint = CreatePaint(true);

					if (ColorHelper.TryParse(stroke, out SKColor color))
					{
						// preserve alpha
						if (color.Alpha == 255)
							strokePaint.Color = color.WithAlpha(strokePaint.Color.Alpha);
						else
							strokePaint.Color = color;
					}
				}

				// stroke attributes
				var strokeDashArray = style.GetString("stroke-dasharray");
				var hasStrokeDashArray = !string.IsNullOrWhiteSpace(strokeDashArray);

				var strokeWidth = style.GetString("stroke-width");
				var hasStrokeWidth = !string.IsNullOrWhiteSpace(strokeWidth);

				var strokeOpacity = style.GetString("stroke-opacity");
				var hasStrokeOpacity = !string.IsNullOrWhiteSpace(strokeOpacity);

				var strokeLineCap = style.GetString("stroke-linecap");
				var hasStrokeLineCap = !string.IsNullOrWhiteSpace(strokeLineCap);

				var strokeLineJoin = style.GetString("stroke-linejoin");
				var hasStrokeLineJoin = !string.IsNullOrWhiteSpace(strokeLineJoin);

				var strokeMiterLimit = style.GetString("stroke-miterlimit");
				var hasStrokeMiterLimit = !string.IsNullOrWhiteSpace(strokeMiterLimit);

				if (strokePaint == null)
				{
					if (hasStrokeDashArray || hasStrokeWidth || hasStrokeOpacity
						|| hasStrokeLineCap || hasStrokeLineJoin)
					{
						strokePaint = CreatePaint(true);
					}
				}

				if (hasStrokeDashArray)
				{
					if ("none".Equals(strokeDashArray, StringComparison.OrdinalIgnoreCase))
					{
						// remove any dash
						if (strokePaint != null)
							strokePaint.PathEffect = null;
					}
					else
					{
						if (strokePaint == null)
							strokePaint = CreatePaint(true);

						// get the dash
						var dashesStrings = strokeDashArray.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
						var dashes = dashesStrings.Select(ReadNumber).ToArray();
						if (dashesStrings.Length % 2 == 1)
							dashes = dashes.Concat(dashes).ToArray();
						// get the offset
						var strokeDashOffset = ReadNumber(style, "stroke-dashoffset", 0);
						// set the effect
						strokePaint.PathEffect = SKPathEffect.CreateDash(dashes.ToArray(), strokeDashOffset);
					}
				}

				if (hasStrokeWidth)
				{
					if (strokePaint == null)
						strokePaint = CreatePaint(true);
					strokePaint.StrokeWidth = ReadNumber(strokeWidth);
				}
				else if (strokePaint != null)
				{
					strokePaint.StrokeWidth = 1f;
				}

				if (hasStrokeOpacity)
				{
					if (strokePaint == null)
						strokePaint = CreatePaint(true);
					strokePaint.Color = strokePaint.Color.WithAlpha((byte)(ReadNumber(strokeOpacity) * 255));
				}

				if (hasStrokeLineCap)
				{
					switch (strokeLineCap)
					{
						case "butt":
							strokePaint.StrokeCap = SKStrokeCap.Butt;
							break;
						case "round":
							strokePaint.StrokeCap = SKStrokeCap.Round;
							break;
						case "square":
							strokePaint.StrokeCap = SKStrokeCap.Square;
							break;
					}
				}

				if (hasStrokeLineJoin)
				{
					switch (strokeLineJoin)
					{
						case "miter":
							strokePaint.StrokeJoin = SKStrokeJoin.Miter;
							break;
						case "round":
							strokePaint.StrokeJoin = SKStrokeJoin.Round;
							break;
						case "bevel":
							strokePaint.StrokeJoin = SKStrokeJoin.Bevel;
							break;
					}
				}

				if (hasStrokeMiterLimit)
				{
					strokePaint.StrokeMiter = ReadNumber(strokeMiterLimit);
				}

				if (strokePaint != null)
				{
					strokePaint.Color = strokePaint.Color.WithAlpha((byte)(strokePaint.Color.Alpha * elementOpacity));
				}
			}

			// fill
			var fill = style.GetString("fill").Trim();
			if (fill.Equals("none", StringComparison.OrdinalIgnoreCase))
			{
				fillPaint = null;
			}
			else
			{
				if (string.IsNullOrEmpty(fill))
				{
					// no change
				}
				else
				{
					if (fillPaint == null)
						fillPaint = CreatePaint();

					if (ColorHelper.TryParse(fill, out SKColor color))
					{
						// preserve alpha
						if (color.Alpha == 255)
							fillPaint.Color = color.WithAlpha(fillPaint.Color.Alpha);
						else
							fillPaint.Color = color;
					}
					else
					{
						var read = false;
						var urlM = fillUrlRe.Match(fill);
						if (urlM.Success)
						{
							var id = urlM.Groups[1].Value.Trim();

							if (defs.TryGetValue(id, out XElement defE))
							{
								switch (defE.Name.LocalName.ToLower())
								{
									case "lineargradient":
										fillPaint.Color = SKColors.Transparent;
										if (!fills.ContainsKey(fill))
											fills.Add(fill, ReadLinearGradient(defE));
										read = true;
										break;
									case "radialgradient":
										fillPaint.Color = SKColors.Transparent;
										if (!fills.ContainsKey(fill))
											fills.Add(fill, ReadRadialGradient(defE));
										read = true;
										break;
								}
								// else try another type (eg: image)
							}
							else
							{
								LogOrThrow($"Invalid fill url reference: {id}");
							}
						}

						if (!read)
						{
							LogOrThrow($"Unsupported fill: {fill}");
						}
					}
				}

				// fill attributes
				var fillOpacity = style.GetString("fill-opacity");
				if (!string.IsNullOrWhiteSpace(fillOpacity))
				{
					if (fillPaint == null)
						fillPaint = CreatePaint();

					fillPaint.Color = fillPaint.Color.WithAlpha((byte)(ReadNumber(fillOpacity) * 255));
				}

				if (fillPaint != null)
				{
					fillPaint.Color = fillPaint.Color.WithAlpha((byte)(fillPaint.Color.Alpha * elementOpacity));
				}
			}
		}

		private SKMatrix ReadTransform(string raw)
		{
			var t = SKMatrix.MakeIdentity();

			if (string.IsNullOrWhiteSpace(raw))
			{
				return t;
			}

			var calls = raw.Trim().Split(new[] { ')' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (var c in calls)
			{
				var args = c.Split(new[] { '(', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
				var nt = SKMatrix.MakeIdentity();
				switch (args[0])
				{
					case "matrix":
						if (args.Length == 7)
						{
							nt.Values = new float[]
							{
								ReadNumber(args[1]), ReadNumber(args[3]), ReadNumber(args[5]),
								ReadNumber(args[2]), ReadNumber(args[4]), ReadNumber(args[6]),
								0, 0, 1
							};
						}
						else
						{
							LogOrThrow($"Matrices are expected to have 6 elements, this one has {args.Length - 1}");
						}
						break;
					case "translate":
						if (args.Length >= 3)
						{
							nt = SKMatrix.MakeTranslation(ReadNumber(args[1]), ReadNumber(args[2]));
						}
						else if (args.Length >= 2)
						{
							nt = SKMatrix.MakeTranslation(ReadNumber(args[1]), 0);
						}
						break;
					case "scale":
						if (args.Length >= 3)
						{
							nt = SKMatrix.MakeScale(ReadNumber(args[1]), ReadNumber(args[2]));
						}
						else if (args.Length >= 2)
						{
							var sx = ReadNumber(args[1]);
							nt = SKMatrix.MakeScale(sx, sx);
						}
						break;
					case "rotate":
						var a = ReadNumber(args[1]);
						if (args.Length >= 4)
						{
							var x = ReadNumber(args[2]);
							var y = ReadNumber(args[3]);
							var t1 = SKMatrix.MakeTranslation(x, y);
							var t2 = SKMatrix.MakeRotationDegrees(a);
							var t3 = SKMatrix.MakeTranslation(-x, -y);
							SKMatrix.Concat(ref nt, ref t1, ref t2);
							SKMatrix.Concat(ref nt, ref nt, ref t3);
						}
						else
						{
							nt = SKMatrix.MakeRotationDegrees(a);
						}
						break;
					default:
						LogOrThrow($"Can't transform {args[0]}");
						break;
				}
				SKMatrix.Concat(ref t, ref t, ref nt);
			}

			return t;
		}

		private SKPath ReadClipPath(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return null;
			}

			SKPath result = null;
			var read = false;
			var urlM = clipPathUrlRe.Match(raw);
			if (urlM.Success)
			{
				var id = urlM.Groups[1].Value.Trim();

				if (defs.TryGetValue(id, out XElement defE))
				{
					result = ReadClipPathDefinition(defE);
					if (result != null)
					{
						read = true;
					}
				}
				else
				{
					LogOrThrow($"Invalid clip-path url reference: {id}");
				}
			}

			if (!read)
			{
				LogOrThrow($"Unsupported clip-path: {raw}");
			}

			return result;
		}

		private SKPath ReadClipPathDefinition(XElement e)
		{
			if (e.Name.LocalName != "clipPath" || !e.HasElements)
			{
				return null;
			}

			var result = new SKPath();

			foreach (var ce in e.Elements())
			{
				var path = ReadElement(ce);
				if (path != null)
				{
					result.AddPath(path);
				}

				else
				{
					LogOrThrow($"SVG element '{ce.Name.LocalName}' is not supported in clipPath.");
				}
			}

			return result;
		}

		private float ReadBaselineShift(XElement element)
		{
			string value = null;
			if (element != null)
			{
				var attrib = element.Attribute("baseline-shift");
				if (attrib != null && !string.IsNullOrWhiteSpace(attrib.Value))
					value = attrib.Value;
				else
				{
					var style = element.Attribute("style");
					if (style != null && !string.IsNullOrWhiteSpace(style.Value))
					{
						value = style.Value.ReadStyle().GetString("baseline-shift");
					}
				}
			}

			return ReadNumber(value);
		}

		private SKRadialGradient ReadRadialGradient(XElement e)
		{
			var centerX = ReadNumber(e.Attribute("cx"), 0.5f);
			var centerY = ReadNumber(e.Attribute("cy"), 0.5f);
			var radius = ReadNumber(e.Attribute("r"), 0.5f);

			//var focusX = ReadOptionalNumber(e.Attribute("fx")) ?? centerX;
			//var focusY = ReadOptionalNumber(e.Attribute("fy")) ?? centerY;         
			//var absolute = e.Attribute("gradientUnits")?.Value == "userSpaceOnUse";

			var tileMode = e.ReadSpreadMethod();
			var stops = ReadStops(e);

			// TODO: check gradientTransform attribute
			// TODO: use absolute

			return new SKRadialGradient(centerX, centerY, radius, stops.Keys.ToArray(), stops.Values.ToArray(), tileMode);
		}

		private SKLinearGradient ReadLinearGradient(XElement e)
		{
			var startX = ReadNumber(e.Attribute("x1"));
			var startY = ReadNumber(e.Attribute("y1"));
			float endX = ReadNumber(e.Attribute("x2"), 1f);
			float endY = ReadNumber(e.Attribute("y2"));

			//var absolute = e.Attribute("gradientUnits")?.Value == "userSpaceOnUse";
			var tileMode = e.ReadSpreadMethod();
			var stops = ReadStops(e);

			// TODO: check gradientTransform attribute
			// TODO: use absolute

			return new SKLinearGradient(startX, startY, endX, endY, stops.Keys.ToArray(), stops.Values.ToArray(), tileMode);
		}

		private XElement ReadDefinition(XElement e)
		{
			var union = new XElement(e.Name);
			union.Add(e.Elements());
			union.Add(e.Attributes());

			var child = ReadHref(e);
			if (child != null)
			{
				union.Add(child.Elements());
				union.Add(child.Attributes().Where(a => union.Attribute(a.Name) == null));
			}

			return union;
		}

		private XElement ReadHref(XElement e)
		{
			var href = e.ReadHrefString()?.Substring(1);
			if (string.IsNullOrEmpty(href) || !defs.TryGetValue(href, out XElement child))
			{
				child = null;
			}
			return child;
		}

		private SortedDictionary<float, SKColor> ReadStops(XElement e)
		{
			var stops = new SortedDictionary<float, SKColor>();

			var ns = e.Name.Namespace;
			foreach (var se in e.Elements(ns + "stop"))
			{
				var style = se.ReadStyle();

				var offset = ReadNumber(style["offset"]);
				var color = SKColors.Black;
				byte alpha = 255;

				if (style.TryGetValue("stop-color", out string stopColor))
				{
					// preserve alpha
					if (ColorHelper.TryParse(stopColor, out color) && color.Alpha == 255)
						alpha = color.Alpha;
				}

				if (style.TryGetValue("stop-opacity", out string stopOpacity))
				{
					alpha = (byte)(ReadNumber(stopOpacity) * 255);
				}

				color = color.WithAlpha(alpha);
				stops[offset] = color;
			}

			return stops;
		}

		private float ReadOpacity(Dictionary<string, string> style)
		{
			return Math.Min(Math.Max(0.0f, ReadNumber(style, "opacity", 1.0f)), 1.0f);
		}

		private float ReadNumber(Dictionary<string, string> style, string key, float defaultValue)
		{
			float value = defaultValue;
			if (style.TryGetValue(key, out string strValue))
			{
				value = ReadNumber(strValue);
			}
			return value;
		}

		private float ReadNumber(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return 0;

			var s = raw.Trim();
			var m = 1.0f;

			if (unitRe.IsMatch(s))
			{
				if (s.EndsWith("in", StringComparison.Ordinal))
				{
					m = PixelsPerInch;
				}
				else if (s.EndsWith("cm", StringComparison.Ordinal))
				{
					m = PixelsPerInch / 2.54f;
				}
				else if (s.EndsWith("mm", StringComparison.Ordinal))
				{
					m = PixelsPerInch / 25.4f;
				}
				else if (s.EndsWith("pt", StringComparison.Ordinal))
				{
					m = PixelsPerInch / 72.0f;
				}
				else if (s.EndsWith("pc", StringComparison.Ordinal))
				{
					m = PixelsPerInch / 6.0f;
				}
				s = s.Substring(0, s.Length - 2);
			}
			else if (percRe.IsMatch(s))
			{
				s = s.Substring(0, s.Length - 1);
				m = 0.01f;
			}

			if (!float.TryParse(s, NumberStyles.Float, icult, out float v))
			{
				v = 0;
			}

			return m * v;
		}

		private float ReadNumber(XAttribute a, float defaultValue) => a == null ? defaultValue : ReadNumber(a.Value);

		private float ReadNumber(XAttribute a) => ReadNumber(a?.Value);

		private float? ReadOptionalNumber(XAttribute a) => a == null ? (float?)null : ReadNumber(a.Value);

		private SKRect ReadRectangle(string s)
		{
			var r = new SKRect();
			var p = s.Split(WS, StringSplitOptions.RemoveEmptyEntries);
			if (p.Length > 0)
				r.Left = ReadNumber(p[0]);
			if (p.Length > 1)
				r.Top = ReadNumber(p[1]);
			if (p.Length > 2)
				r.Right = r.Left + ReadNumber(p[2]);
			if (p.Length > 3)
				r.Bottom = r.Top + ReadNumber(p[3]);
			return r;
		}

		private SKPaint CreatePaint(bool stroke = false)
        {
            var strokePaint = new SKPaint
            {
                IsAntialias = true,
                IsStroke = stroke,
                Color = SKColors.Black
            };

            if (stroke)
            {
                strokePaint.StrokeWidth = 1f;
                strokePaint.StrokeMiter = 4f;
                strokePaint.StrokeJoin = SKStrokeJoin.Miter;
                strokePaint.StrokeCap = SKStrokeCap.Butt;
            }

            return strokePaint;
        }

		private void LogOrThrow(string message)
        {
            if (ThrowOnUnsupportedElement)
                throw new NotSupportedException(message);

            Debug.WriteLine(message);
        }
	}
}
