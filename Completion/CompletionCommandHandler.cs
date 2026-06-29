using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeWeave
{
	internal class CompletionCommandHandler : IOleCommandTarget
	{
		public static int Utf16OffsetToUtf8Offset(string str, int utf16Offset)
		{
			return Encoding.UTF8.GetByteCount(str.ToCharArray(), 0, utf16Offset);
		}

		public static int Utf16OffsetToUtf8Offset(ITextSnapshot text, int utf16Offset)
		{
			int offset = 0;
			char[] chars = new char[1];
			for (int i = 0; i < utf16Offset; ++i)
			{
				chars[0] = text[i];
				offset += Encoding.UTF8.GetByteCount(chars);
			}
			return offset;
		}

		public static int Utf8OffsetToUtf16Offset(string str, int utf8Offset)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(str);
			return Encoding.UTF8.GetString(bytes.Take(utf8Offset).ToArray()).Length;
		}

		public static int Utf8OffsetToUtf16Offset(ITextSnapshot text, int utf8Offset)
		{
			int offset = 0;
			char[] chars = new char[1];
			int i = 0;
			for (; i < text.Length; ++i)
			{
				chars[0] = text[i];
				offset += Encoding.UTF8.GetByteCount(chars);
				if (utf8Offset <= offset)
				{
					break;
				}
			}
			return i;
		}

		internal CompletionCommandHandler(IVsTextView textViewAdapter, ITextView textView, CompletionCommandHandlerProvider provider)
		{
			vsTextView_ = textViewAdapter;
			textView_ = textView;
			provider_ = provider;

			//add the command to the command chain
			textViewAdapter.AddCommandFilter(this, out nextCommandHandler_);
		}

		public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (!VsShellUtilities.IsInAutomationFunction(provider_.ServiceProvider))
			{
				if (pguidCmdGroup == PackageGuids.CodeWeave && 0<cCmds)
				{
					// make the Insert Snippet command appear on the context menu 
					if (prgCmds[0].cmdID == (uint)PackageIds.CommandCompletion)
					{
						prgCmds[0].cmdf = (int)Microsoft.VisualStudio.OLE.Interop.Constants.MSOCMDF_ENABLED | (int)Microsoft.VisualStudio.OLE.Interop.Constants.MSOCMDF_SUPPORTED;
						return VSConstants.S_OK;
					}
				}
			}
			return nextCommandHandler_.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
		}

		public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
		{
			ThreadHelper.ThrowIfNotOnUIThread();
			if (VsShellUtilities.IsInAutomationFunction(provider_.ServiceProvider))
			{
				return nextCommandHandler_.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
			}
			//make a copy of this so we can look at it after forwarding some commands
			uint commandID = nCmdID;
			char typedChar = char.MinValue;
			//make sure the input is a char before getting it
			if (pguidCmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
			{
				typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
			}

			//check for a commit character
			if (!hasCompletionUpdated_ && nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB)
			{

                CompletionTagger tagger = GetTagger();

				if (tagger != null)
				{
					if (tagger.IsSuggestionActive() && tagger.CompleteText())
					{
						ClearCompletionSessions();
						return VSConstants.S_OK;
					}
					else
					{
						tagger.ClearSuggestion();
					}
				}

			}
			else if (nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN || nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL)
			{
                CompletionTagger tagger = GetTagger();
				if (tagger != null)
				{
					tagger.ClearSuggestion();
				}
			}

			CheckSuggestionUpdate(nCmdID);

			//pass along the command so the char is added to the buffer
			int retVal = nextCommandHandler_.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
			if (InProcessingCompletionTask())
			{
				return retVal;
			}
			{
                bool handled = false;
				//gets completions on added character or deletions
				if (pguidCmdGroup == PackageGuids.CodeWeave && commandID == (uint)PackageIds.CommandCompletion)
				{
					_ = Task.Run(() => GetCompletionsAsync());
					handled = true;
				}
				else
				{
					if (!typedChar.Equals(char.MinValue) || commandID == (uint)VSConstants.VSStd2KCmdID.RETURN)
					{
						_ = Task.Run(() => GetCompletionsAsync());
						handled = true;
					}
					else if (commandID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE || commandID == (uint)VSConstants.VSStd2KCmdID.DELETE)
					{
						_ = Task.Run(() => GetCompletionsAsync());
						handled = true;
					}
				}
				if (handled)
				{
					return VSConstants.S_OK;
				}
			}
			return retVal;
		}

		private CompletionTagger GetTagger()
		{
			Type key = typeof(CompletionTagger);
			Microsoft.VisualStudio.Utilities.PropertyCollection props = textView_.TextBuffer.Properties;
			if (props.ContainsProperty(key))
			{
				return props.GetProperty<CompletionTagger>(key);
			}
			else
			{
				return null;
			}
		}

		private bool InProcessingCompletionTask()
		{
			return (null != completionTask_ && !completionTask_.IsCompleted);
		}

		private async Task GetCompletionsAsync()
		{
			int lineNumber;
			int characterNumber;
			_ = vsTextView_.GetCaretPos(out lineNumber, out characterNumber);

			DocumentView documentView = await vsTextView_.ToDocumentViewAsync();
			if (null == documentView)
			{
				return;
			}

			CodeWeavePackage package;
			if(!CodeWeavePackage.TryGetPackage(out package))
			{
				return;
            }
			OptionPage optionPage = package.OptionPage;
			if(null == optionPage)
			{
				return;
			}
			DateTime currentTime = DateTime.Now;
			(string prefix, string suffix, int caretPosition) = CodeUtil.GetCodeAround(documentView);
			if (string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(suffix))
			{
				return;
			}
			int completionInterval = optionPage.CompletionIntervalInMilliseconds;
			int prefixTokens = optionPage.PrefixTokens;
            int suffixTokens = optionPage.SuffixTokens;
            int maxTokens = optionPage.MaxTokens;

			hasCompletionUpdated_ = false;
			if (!InProcessingCompletionTask())
			{
				{// Check completion interval
					long differenceTime = currentTime <= lastCompletionTime_ ? 0 : (long)((currentTime-lastCompletionTime_).TotalMilliseconds);
					if (differenceTime < completionInterval)
					{
						return;
					}
				}
				using CodeWeavePackage.LlamaCompletionEngineWrapper engine = package.GetLlamaEngine();
				if(null == engine.Engine)
				{
					return;
				}
				string response = string.Empty;
				try
				{
					CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
					completionTask_ = engine.Engine.GenerateAsync(prefix, suffix, prefixTokens, suffixTokens, maxTokens, cancellationTokenSource.Token);
					response = await completionTask_;
				}catch(Exception ex)
				{
					await Log.OutputAsync(ex.Message);
					return;
				}
				completionTask_ = null;
				response = CodeUtil.GetLine(response);
				if (!string.IsNullOrWhiteSpace(response))
				{
					int newLineNumber;
					int newCharacterNumber;
					int resCaretPos = vsTextView_.GetCaretPos(out newLineNumber, out newCharacterNumber);
					if (resCaretPos != VSConstants.S_OK || (lineNumber != newLineNumber) || (characterNumber != newCharacterNumber))
					{
						return;
					}
					CompletionTagger tagger = GetTagger();
					if (null != tagger)
					{
						await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
						tagger.SetSuggestion(response, IsInline(newLineNumber), newCharacterNumber);
						lastCompletionTime_ = DateTime.Now;
					}
				}
			}
		}

		private bool IsInline(int lineNumber)
		{
			string text = textView_.TextSnapshot.GetLineFromLineNumber(lineNumber).GetText();
			return !String.IsNullOrWhiteSpace(text);
		}

#if false
		private async Task ShowCompletionAsync(String text, int lineNumber, int characterNumber)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			SnapshotPoint? caretPoint = textView_.Caret.Position.Point.GetPoint(textBuffer => (!textBuffer.ContentType.IsOfType("projection")), PositionAffinity.Predecessor);
			if (!caretPoint.HasValue)
			{
				return;
			}

			int newLineNumber;
			int newCharacterNumber;
			int resCaretPos = vsTextView_.GetCaretPos(out newLineNumber, out newCharacterNumber);

			if (resCaretPos != VSConstants.S_OK || (lineNumber != newLineNumber) || (characterNumber != newCharacterNumber))
			{
				return;
			}

			CompletionTagger tagger = GetTagger();
			if (null != tagger)
			{
				await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
				tagger.SetSuggestion(text, IsInline(newLineNumber), newCharacterNumber);
			}
		}
#endif

		void CheckSuggestionUpdate(uint nCmdID)
		{
			switch (nCmdID)
			{
				case ((uint)VSConstants.VSStd2KCmdID.UP):
				case ((uint)VSConstants.VSStd2KCmdID.DOWN):
				case ((uint)VSConstants.VSStd2KCmdID.PAGEUP):
				case ((uint)VSConstants.VSStd2KCmdID.PAGEDN):
					if (provider_.CompletionBroker.IsCompletionActive(textView_))
					{
						hasCompletionUpdated_ = true;
					}

					break;
				case ((uint)VSConstants.VSStd2KCmdID.TAB):
				case ((uint)VSConstants.VSStd2KCmdID.RETURN):
					hasCompletionUpdated_ = false;
					break;
			}
		}

		private void ClearCompletionSessions()
		{
			provider_.CompletionBroker.DismissAllSessions(textView_);
		}

		private IOleCommandTarget nextCommandHandler_;
		private IVsTextView vsTextView_;
		private ITextView textView_;
		private CompletionCommandHandlerProvider provider_;
		//private ICompletionSession session_;
		private bool hasCompletionUpdated_;
		private Task<string> completionTask_;
		private DateTime lastCompletionTime_ = DateTime.MinValue;
	}
}
