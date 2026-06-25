using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System.Text;

namespace CodeWeave
{
    internal static class CodeUtil
    {
#if false
        private static readonly vsCMElement[] AcceptElements = {
            vsCMElement.vsCMElementOther,
            vsCMElement.vsCMElementClass,
            vsCMElement.vsCMElementFunction,
            vsCMElement.vsCMElementVariable,
            vsCMElement.vsCMElementNamespace,
            vsCMElement.vsCMElementParameter,
            vsCMElement.vsCMElementEnum,
            vsCMElement.vsCMElementStruct,
            vsCMElement.vsCMElementUnion,
            vsCMElement.vsCMElementLocalDeclStmt,
            vsCMElement.vsCMElementFunctionInvokeStmt,
            vsCMElement.vsCMElementAssignmentStmt,
            vsCMElement.vsCMElementDefineStmt,
            vsCMElement.vsCMElementTypeDef,
            vsCMElement.vsCMElementIncludeStmt,
            vsCMElement.vsCMElementMacro,
        };

        private static readonly vsCMElement[] IgnoredElements = {
            vsCMElement.vsCMElementVCBase,
        };
#endif
        public enum TypeLanguage
        {
            C_Cpp,
            CSharp,
            Others
        }

        public static TypeLanguage GetLanguageFromDocument(EnvDTE.Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (document.Language)
            {
                case "C/C++":
                    return TypeLanguage.C_Cpp;
                case "CSharp":
                    return TypeLanguage.CSharp;
                default:
                    return TypeLanguage.Others;
            }
        }

        public static TypeLanguage GetLanguage(Microsoft.VisualStudio.Utilities.IContentType contentType)
        {
            if (contentType.IsOfType("C/C++"))
            {
                return TypeLanguage.C_Cpp;
            }
            else if (contentType.IsOfType("CSharp"))
            {
                return TypeLanguage.CSharp;
            }
            else
            {
                return TypeLanguage.Others;
            }
        }

        public static string NormalizeToLinuxLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text)) {
                return text;
            }

            StringBuilder stringBuilder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; ++i)
            {
                char c = text[i];

                if (c == '\r')
                {
                    // Skip the '\n' if this is CRLF
                    if (i + 1 < text.Length && text[i + 1] == '\n') {
                        i++;
                    }
                    stringBuilder.Append('\n');
                }
                else
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString();
        }

        public static (string, string, int) GetCodeAround(DocumentView documentView)
        {
            System.Diagnostics.Debug.Assert(null != documentView);
            if (null == documentView?.TextView)
            {
                return (string.Empty, string.Empty, 0);
            }

            ITextView textView = documentView.TextView;
            ITextSnapshot snapshot = textView.TextSnapshot;

            // Caret position in current snapshot
            SnapshotPoint caretPoint = textView.Caret.Position.BufferPosition;

            // Ensure point is in this snapshot (important with projection scenarios)
            if (caretPoint.Snapshot != snapshot)
            {
                caretPoint = caretPoint.TranslateTo(snapshot, PointTrackingMode.Positive);
            }

            int pos = caretPoint.Position;
            string prefix = snapshot.GetText(0, pos);
            string suffix = snapshot.GetText(pos, snapshot.Length - pos);
            prefix = NormalizeToLinuxLineEndings(prefix);
            suffix = NormalizeToLinuxLineEndings(suffix);
            return (prefix, suffix, pos);
        }

        public static string GetLine(string text)
        {
            int i;
            for(i=0; i<text.Length; ++i)
            {
                if ('\n' == text[i] || '\r' == text[i])
                {
                    break;
                }
            }
            return text.Substring(0, i);
        }

        public static (string, string) GetNextCompletion(string text)
        {
            System.Diagnostics.Debug.Assert(null != text);
            int wordStart = 0;
            for(int i=0; i<text.Length; ++i)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    break;
                }
                ++wordStart;
			}
			int wordEnd = wordStart;
			for (int i = wordEnd; i < text.Length; ++i)
			{
				if (char.IsWhiteSpace(text[i]))
				{
					break;
				}
				++wordEnd;
			}
            string prefix = text.Substring(0, wordEnd);
			string suffix = text.Substring(wordEnd);
            return (prefix, suffix);
		}
    }
}

