using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodeWeave
{
	[ComVisible(true)]
	public class OptionPage : Microsoft.VisualStudio.Shell.DialogPage
    {
        [Category("OpenAI")]
        [DisplayName("API Key")]
        [Description("OpenAI API key used for the chat agent.")]
        [PasswordPropertyText(true)]
        public string OpenAIApiKey
        {
            get { return openAIApiKey_; }
            set { openAIApiKey_ = value; }
        }

        [Category("General")]
        [DisplayName("Load Setting File")]
        [Description("Load \"_codeweave.xml\".")]
        public bool LoadSettingFile
        {
            get { return loadSettingFile_; }
            set { loadSettingFile_ = value; }
        }

        [Category("Debug")]
        [DisplayName("Debug Log")]
        [Description("Output debug logs")]
        [DefaultValue(false)]
        public bool OutputDebugLog
        {
            get { return outputDebugLog_; }
            set { outputDebugLog_ = value;}
        }

		[Category("Model")]
		[DisplayName("ModelPath")]
		[Description("Path to model")]
		public string ModelPath
		{
			get { return modelPath_; }
			set { modelPath_ = value; }
		}

		[Category("Model")]
		[DisplayName("Number of GPU Layers")]
		[Description("Number of GPU layers to use.")]
		public int NumberOfGpuLayers
		{
			get { return numberOfGpuLayers_; }
			set { numberOfGpuLayers_ = value; }
		}

		[Category("Model")]
		[DisplayName("Context Size")]
		[Description("Context size for the model.")]
		public int ContextSize
		{
			get { return contextSize_; }
			set { contextSize_ = value; }
		}

		[Category("Model")]
		[DisplayName("Timeout")]
		[Description("Timeout for interacting with AI in seconds.")]
		public int Timeout
		{
			get { return timeout_; }
			set { timeout_ = value; }
		}

		[Category("Model")]
		[DisplayName("Temperature")]
		[Description("Temperature")]
		public float Temperature
		{
			get { return temperature_; }
			set { temperature_ = value; }
		}

		[Category("Model")]
		[DisplayName("top_p")]
		[Description("top_p")]
		public float TopP
		{
			get { return topP_; }
			set { topP_ = value; }
		}

		[Category("Model")]
		[DisplayName("top_k")]
		[Description("top_k")]
		public int TopK 
		{
			get { return topK_; }
			set { topK_ = value; }
		}

		[Category("Model")]
		[DisplayName("Prefix Tokens")]
		[Description("Number of tokens to include as prefix")]
		public int PrefixTokens
		{
			get { return prefixTokens_; }
			set { prefixTokens_ = value; }
		}

		[Category("Model")]
		[DisplayName("Suffix Lines")]
		[Description("Number of lines to include as suffix")]
		public int SuffixTokens
		{
			get { return suffixTokens_; }
			set { suffixTokens_ = value; }
		}

		[Category("Model")]
		[DisplayName("Max Tokens")]
		[Description("Maximum number of tokens")]
		public int MaxTokens
		{
			get { return maxTokens_; }
			set { maxTokens_ = value; }
		}

        [Category("Model")]
        [DisplayName("Trigger Delay (ms)")]
        [Description("Trigger Delay in milliseconds")]
        public int TriggerDelayMs
        {
            get { return triggerDelayMs_; }
            set { triggerDelayMs_ = value; }
        }

		[Category("Model")]
        [DisplayName("Completion Interval (ms)")]
        [Description("Completion Interval in milliseconds")]
        public int CompletionIntervalInMilliseconds
        {
            get { return completionIntervalInMilliseconds_; }
            set { completionIntervalInMilliseconds_ = value; }
        }

        private string openAIApiKey_ = string.Empty;
        private bool loadSettingFile_ = true;
        private bool outputDebugLog_ = false;
		private string modelPath_ = "Resources\\Qwen2.5-Coder-1.5B-CodeFIM.IQ4_XS.gguf";
		private int numberOfGpuLayers_ = 0;
		private int contextSize_ = 65536;
		private int timeout_ = 30;
		private float temperature_ = 0.1f;
		private float topP_ = 0.9f;
        private int topK_ = 50;
        private int prefixTokens_ = 32768;
        private int suffixTokens_ = 16384;
		private int maxTokens_ = 16;
        private int triggerDelayMs_ = 500;
		private int completionIntervalInMilliseconds_ = 300;
    }
}
