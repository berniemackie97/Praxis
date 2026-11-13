using System.Net;

namespace Praxis.Application.Finance.Ofx;

/// <summary>
/// Reads OFX 1.x SGML (leaf elements without end tags) and OFX 2.x XML into one element tree.
/// A tag followed by text is a leaf; a tag followed by another tag opens an aggregate. End tags
/// close the nearest matching open aggregate, so missing or extra end tags cannot misplace
/// later elements. Depth and element counts are bounded.
/// </summary>
public static class OfxMarkupReader
{
    private const int MaximumDepth = 32;
    private const int MaximumElements = 2_000_000;

    public static OfxElement? Read(string text)
    {
        int start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        OfxElement root = new("#document");
        Stack<OfxElement> open = new();
        open.Push(root);
        int elements = 0;
        int position = start;
        while (position < text.Length)
        {
            int tagStart = text.IndexOf('<', position);
            if (tagStart < 0)
            {
                break;
            }

            int tagEnd = text.IndexOf('>', tagStart + 1);
            if (tagEnd < 0)
            {
                return null;
            }

            string tag = text[(tagStart + 1)..tagEnd].Trim();
            position = tagEnd + 1;
            if (tag.Length == 0 || tag[0] is '?' or '!')
            {
                continue;
            }

            if (tag[0] == '/')
            {
                Close(open, tag[1..].Trim().ToUpperInvariant());
                continue;
            }

            bool selfClosing = tag.EndsWith('/');
            string name = (selfClosing ? tag[..^1] : tag).Split(' ', 2)[0].Trim().ToUpperInvariant();
            if (name.Length == 0 || ++elements > MaximumElements)
            {
                return null;
            }

            int nextTag = text.IndexOf('<', position);
            string between = (nextTag < 0 ? text[position..] : text[position..nextTag]).Trim();
            if (selfClosing)
            {
                open.Peek().Add(new OfxElement(name, ""));
            }
            else if (between.Length > 0)
            {
                open.Peek().Add(new OfxElement(name, WebUtility.HtmlDecode(between)));
                position = nextTag < 0 ? text.Length : nextTag;
                if (nextTag >= 0 && IsEndTag(text, nextTag, name, out int afterEnd))
                {
                    position = afterEnd;
                }
            }
            else
            {
                if (open.Count > MaximumDepth)
                {
                    return null;
                }

                OfxElement aggregate = new(name);
                open.Peek().Add(aggregate);
                open.Push(aggregate);
            }
        }

        return root.Child("OFX");
    }

    private static void Close(Stack<OfxElement> open, string name)
    {
        if (!open.Any(element => element.Name == name))
        {
            return;
        }

        while (open.Count > 1)
        {
            if (open.Pop().Name == name)
            {
                return;
            }
        }
    }

    private static bool IsEndTag(string text, int tagStart, string name, out int afterEnd)
    {
        afterEnd = tagStart;
        int tagEnd = text.IndexOf('>', tagStart);
        if (tagEnd < 0 || tagStart + 1 >= text.Length || text[tagStart + 1] != '/')
        {
            return false;
        }

        if (!text[(tagStart + 2)..tagEnd].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        afterEnd = tagEnd + 1;
        return true;
    }
}
