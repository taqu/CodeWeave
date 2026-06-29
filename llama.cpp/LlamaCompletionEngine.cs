using Murmur;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace CodeWeave
{
    /// <summary>
    /// Manages the llama.cpp model/context/sampler lifecycle and exposes
    /// a single <see cref="GenerateAsync"/> entry point for FIM code completion.
    /// </summary>
    public sealed class LlamaCompletionEngine : IDisposable
    {
        private IntPtr _model = IntPtr.Zero;
        private IntPtr _context = IntPtr.Zero;
        private IntPtr _sampler = IntPtr.Zero;
        private IntPtr _vocab = IntPtr.Zero;

        private int _fimPre = -1;
        private int _fimSuf = -1;
        private int _fimMid = -1;
        private int _fimSep = -1;
        private int _linefeed = -1;

        /// <summary>
        /// The text form of the model's file-separator special token (e.g.
        /// "&lt;|file_sep|&gt;"), or <c>null</c> if the model has no such token.
        /// Use this string as a file-boundary delimiter in multi-file prompts;
        /// because tokenisation runs with parseSpecial=true it will be encoded
        /// as a single special token rather than raw characters.
        /// </summary>
        public string FileSepToken { get; private set; }

        private readonly SemaphoreSlim semaphore_ = new SemaphoreSlim(1, 1);

        // Tokens that were last decoded into the KV cache (prompt only, not generated tokens).
        // Used to find the common prefix with the next request so we only decode the delta.
        private int[] _prevPromptTokens = Array.Empty<int>();

        public bool IsLoaded => _model != IntPtr.Zero;
        public string LoadedModelPath { get; private set; } = string.Empty;

        // -------------------------------------------------------------------------
        // Loading
        // -------------------------------------------------------------------------

        /// <summary>
        /// Loads the model and creates the inference context.
        /// Safe to call from a background thread.
        /// </summary>
        public void Load(string modelPath, int nGpuLayers, int nCtx, float temprature, float top_p = 0.9f, int top_k = 50)
        {
            Unload();
            if (!LlamaInterop.Initialized)
            {
                return;
            }

            LlamaInterop.LlamaModelParams mparams = LlamaInterop.DefaultModelParams(nGpuLayers);
            _model = LlamaInterop.llama_model_load_from_file(modelPath, mparams);
            if (_model == IntPtr.Zero)
                throw new InvalidOperationException($"llama_model_load_from_file failed for: {modelPath}");

            _vocab = LlamaInterop.llama_model_get_vocab(_model);

            int threads = Math.Max(1, Environment.ProcessorCount);
            LlamaInterop.LlamaContextParams cparams = LlamaInterop.DefaultContextParams(nCtx, threads);
            _context = LlamaInterop.llama_init_from_model(_model, cparams);
            if (_context == IntPtr.Zero)
            {
                LlamaInterop.llama_model_free(_model);
                _model = IntPtr.Zero;
                return;
            }

            // Build sampler chain: min-p(0.05) → temp(0.2) → dist
            LlamaInterop.LlamaSamplerChainParams sparams = new LlamaInterop.LlamaSamplerChainParams { no_perf = 1 };
            _sampler = LlamaInterop.llama_sampler_chain_init(sparams);
            LlamaInterop.llama_sampler_chain_add(_sampler,
                LlamaInterop.llama_sampler_init_top_k(top_k));
            LlamaInterop.llama_sampler_chain_add(_sampler,
                LlamaInterop.llama_sampler_init_top_p(top_p, (UIntPtr)1));
            LlamaInterop.llama_sampler_chain_add(_sampler,
                LlamaInterop.llama_sampler_init_temp(temprature));
            LlamaInterop.llama_sampler_chain_add(_sampler,
                LlamaInterop.llama_sampler_init_dist(LlamaInterop.LLAMA_DEFAULT_SEED));

            // Cache FIM special tokens (-1 means "not present in this model")
            _fimPre = LlamaInterop.llama_vocab_fim_pre(_vocab);
            _fimSuf = LlamaInterop.llama_vocab_fim_suf(_vocab);
            _fimMid = LlamaInterop.llama_vocab_fim_mid(_vocab);
            _fimSep = LlamaInterop.llama_vocab_fim_sep(_vocab);
            {
                byte[] utf8 = Encoding.UTF8.GetBytes("\n");
                unsafe
                {
                    byte[] tokens = new byte[utf8.Length];
                    fixed (byte* textPtr = utf8)
                    fixed (byte* tokensPtr = tokens)
                    {
                        int length = LlamaInterop.llama_tokenize(_vocab, (IntPtr)textPtr, utf8.Length, (IntPtr)tokensPtr, utf8.Length, 0,0);
                        _linefeed = length == utf8.Length? tokens[0] : -1;
                    }
                }
            }
            FileSepToken = _fimSep >= 0
                ? LlamaInterop.TokenToPiece(_vocab, _fimSep, special: true)
                : null;

            LoadedModelPath = modelPath;
        }

        public void Unload()
        {
            if (_sampler != IntPtr.Zero) { LlamaInterop.llama_sampler_free(_sampler); _sampler = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { LlamaInterop.llama_free(_context); _context = IntPtr.Zero; }
            if (_model != IntPtr.Zero) { LlamaInterop.llama_model_free(_model); _model = IntPtr.Zero; }
            _vocab = IntPtr.Zero;
            LoadedModelPath = string.Empty;
            FileSepToken = null;
            _prevPromptTokens = Array.Empty<int>();
        }

        public void Dispose() => Unload();

        // -------------------------------------------------------------------------
        // Cache key
        // -------------------------------------------------------------------------

        // Encodes prefix + NUL + suffix as UTF-8 and hashes with XxHash3,
        // returning a ulong suitable as a dictionary key.
        private static ulong ComputeCacheKey(string prefix, string suffix)
        {
            Encoding enc = Encoding.UTF8;
            int pLen = enc.GetByteCount(prefix);
            int sLen = enc.GetByteCount(suffix);
            byte[] buf = new byte[pLen + 1 + sLen];  // prefix + NUL + suffix
            enc.GetBytes(prefix, 0, prefix.Length, buf, 0);
            // buf[pLen] = 0 already (default)
            enc.GetBytes(suffix, 0, suffix.Length, buf, pLen + 1);
            HashAlgorithm murmur128 = MurmurHash.Create128(managed: true);
            byte[] hash = murmur128.ComputeHash(buf);
            ulong high = BitConverter.ToUInt64(hash, 0);
            ulong low = BitConverter.ToUInt64(hash, 8);
            return high ^ low;
        }

        // -------------------------------------------------------------------------
        // Generation
        // -------------------------------------------------------------------------

        /// <summary>
        /// Generates a code completion for the given <paramref name="prefix"/> /
        /// <paramref name="suffix"/> pair using FIM when available.
        /// Serialised via a semaphore so only one request runs at a time.
        /// </summary>
        public async Task<string> GenerateAsync(string prefix, string suffix, int prefixTokens, int suffixTokens, int maxTokens, CancellationToken cancellationToken)
        {
            if (!IsLoaded){
                return string.Empty;
            }
            bool hasFim = 0 <= _fimPre && 0 <= _fimSuf && 0 <= _fimMid;
            if (!hasFim)
            {
                return string.Empty;
            }

            await semaphore_.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string result = await Task.Run(
                    () => GenerateInternal(prefix, suffix, prefixTokens, suffixTokens, maxTokens, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                return result;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                semaphore_.Release();
            }
        }

        private string GenerateInternal(string prefix, string suffix, int prefixTokens, int suffixTokens, int maxTokens, CancellationToken cancellationToken)
        {
            // Build token sequence
            List<int> allTokens = new List<int>();
            {
                allTokens.Add(_fimPre);
                allTokens.AddRange(LlamaInterop.Tokenize(_vocab, prefix, addSpecial: false, parseSpecial: true));
                allTokens.Add(_fimSuf);
                allTokens.AddRange(LlamaInterop.Tokenize(_vocab, suffix, addSpecial: false, parseSpecial: true));
                allTokens.Add(_fimMid);
            }

            // Find how many leading tokens are shared with the previous request.
            // Those are already decoded in the KV cache — only the delta needs decoding.
            int[] prev = _prevPromptTokens;
            int commonLen = 0;
            int maxCommon = Math.Min(prev.Length, allTokens.Count);
            while (commonLen < maxCommon && prev[commonLen] == allTokens[commonLen])
                commonLen++;

            // Trim (or fully clear) the KV cache to the common prefix length.
            IntPtr mem = LlamaInterop.llama_get_memory(_context);
            if (commonLen > 0) {
                LlamaInterop.llama_memory_seq_rm(mem, 0, commonLen, -1);
            }
            else {
                LlamaInterop.llama_memory_clear(mem, 0);
            }

            // Decode only the new (delta) tokens after the common prefix.
            int batchMax = 512;
            for (int i = commonLen; i < allTokens.Count; i += batchMax)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(batchMax, allTokens.Count - i);
                int[] chunk = new int[count];
                allTokens.CopyTo(i, chunk, 0, count);

                if (DecodeBatch(chunk) != 0)
                {
                    // KV cache state is unknown after a failed decode — force full
                    // re-decode on the next request.
                    _prevPromptTokens = Array.Empty<int>();
                    return string.Empty;
                }
            }

            // Remember the prompt tokens for the next request.
            _prevPromptTokens = allTokens.ToArray();

            // Token-by-token generation
            StringBuilder stringBuilder = new StringBuilder(1024);
            int[] single = new int[1];
            for (int gen = 0; gen < maxTokens; gen++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int newToken = LlamaInterop.llama_sampler_sample(_sampler, _context, -1);

                if (LlamaInterop.llama_vocab_is_eog(_vocab, newToken)){
                    break;
                }

                // Stop on FIM suffix/mid tokens (model signalling end-of-completion)
                if (newToken == _fimSuf || newToken == _fimPre){
                    break;
                }
                string piece = LlamaInterop.TokenToPiece(_vocab, newToken);
                stringBuilder.Append(piece);

                // Decode the new token to advance the KV cache
                single[0] = newToken;
                if(DecodeBatch(single) != 0){
                    break;
                }
            }
            return stringBuilder.ToString();
        }

        private int DecodeBatch(int[] tokens)
        {
            GCHandle handle = GCHandle.Alloc(tokens, GCHandleType.Pinned);
            try
            {
                LlamaInterop.LlamaBatch batch = LlamaInterop.llama_batch_get_one(handle.AddrOfPinnedObject(), tokens.Length);
                return LlamaInterop.llama_decode(_context, batch);
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
