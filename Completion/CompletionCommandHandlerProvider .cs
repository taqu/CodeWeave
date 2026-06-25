using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace CodeWeave
{
	[Export(typeof(IVsTextViewCreationListener))]
	[Name("CompletionCommandHandlerProvider")]
	[ContentType("text")]
	[TextViewRole(PredefinedTextViewRoles.Document)]
	internal class CompletionCommandHandlerProvider : IVsTextViewCreationListener
	{
		[Import]
		internal IVsEditorAdaptersFactoryService AdapterService = null;
		[Import]
		internal ICompletionBroker CompletionBroker { get; set; }
		[Import]
		internal SVsServiceProvider ServiceProvider { get; set; }

		[Import]
		internal ITextDocumentFactoryService documentFactory = null;

		public void VsTextViewCreated(IVsTextView textViewAdapter)
		{
			ITextView textView = AdapterService.GetWpfTextView(textViewAdapter);
			if(null == textView)
			{
				return;
			}
			if(CodeUtil.GetLanguage(textView.TextDataModel.ContentType) != CodeUtil.TypeLanguage.C_Cpp)
            {
                return;
            }
			Func<CompletionCommandHandler> createCommandHandler = delegate () { return new CompletionCommandHandler(textViewAdapter, textView, this); };
			textView.Properties.GetOrCreateSingletonProperty(createCommandHandler);
		}
	}
}
