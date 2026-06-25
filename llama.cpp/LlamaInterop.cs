using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeWeave
{
    // P/Invoke bindings for llama.dll (x64 MSVC ABI, Windows).
    // Struct field offsets are hand-computed for x64 MSVC alignment rules.
    public static class LlamaInterop
    {
#if DEBUG
        public const string Dll = "llama.dll";
#else
        public const string Dll = "llama.dll";
#endif

        // -------------------------------------------------------------------------
        // Structs
        // -------------------------------------------------------------------------

        // sizeof = 72, alignof = 8
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        internal struct LlamaModelParams
        {
            [FieldOffset(0)] public IntPtr devices;
            [FieldOffset(8)] public IntPtr tensor_buft_overrides;
            [FieldOffset(16)] public int n_gpu_layers;
            [FieldOffset(20)] public int split_mode;
            [FieldOffset(24)] public int main_gpu;
            // offset 28: 4-byte padding (align pointer to 8)
            [FieldOffset(32)] public IntPtr tensor_split;
            [FieldOffset(40)] public IntPtr progress_callback;
            [FieldOffset(48)] public IntPtr progress_callback_user_data;
            [FieldOffset(56)] public IntPtr kv_overrides;
            [FieldOffset(64)] public byte vocab_only;
            [FieldOffset(65)] public byte use_mmap;
            [FieldOffset(66)] public byte use_direct_io;
            [FieldOffset(67)] public byte use_mlock;
            [FieldOffset(68)] public byte check_tensors;
            [FieldOffset(69)] public byte use_extra_bufts;
            [FieldOffset(70)] public byte no_host;
            [FieldOffset(71)] public byte no_alloc;
        }

        // sizeof = 136, alignof = 8
        [StructLayout(LayoutKind.Explicit, Size = 136)]
        internal struct LlamaContextParams
        {
            [FieldOffset(0)] public uint n_ctx;
            [FieldOffset(4)] public uint n_batch;
            [FieldOffset(8)] public uint n_ubatch;
            [FieldOffset(12)] public uint n_seq_max;
            [FieldOffset(16)] public int n_threads;
            [FieldOffset(20)] public int n_threads_batch;
            [FieldOffset(24)] public int rope_scaling_type; // -1 = unspecified
            [FieldOffset(28)] public int pooling_type;      // -1 = unspecified
            [FieldOffset(32)] public int attention_type;    // -1 = unspecified
            [FieldOffset(36)] public int flash_attn_type;   // -1 = auto
            [FieldOffset(40)] public float rope_freq_base;
            [FieldOffset(44)] public float rope_freq_scale;
            [FieldOffset(48)] public float yarn_ext_factor;
            [FieldOffset(52)] public float yarn_attn_factor;
            [FieldOffset(56)] public float yarn_beta_fast;
            [FieldOffset(60)] public float yarn_beta_slow;
            [FieldOffset(64)] public uint yarn_orig_ctx;
            [FieldOffset(68)] public float defrag_thold;
            [FieldOffset(72)] public IntPtr cb_eval;
            [FieldOffset(80)] public IntPtr cb_eval_user_data;
            [FieldOffset(88)] public int type_k;            // 1 = F16
            [FieldOffset(92)] public int type_v;            // 1 = F16
            [FieldOffset(96)] public IntPtr abort_callback;
            [FieldOffset(104)] public IntPtr abort_callback_data;
            [FieldOffset(112)] public byte embeddings;
            [FieldOffset(113)] public byte offload_kqv;
            [FieldOffset(114)] public byte no_perf;
            [FieldOffset(115)] public byte op_offload;
            [FieldOffset(116)] public byte swa_full;
            [FieldOffset(117)] public byte kv_unified;
            // offset 118-119: 2-byte padding (align pointer to 8)
            [FieldOffset(120)] public IntPtr samplers;
            [FieldOffset(128)] public IntPtr n_samplers;        // size_t
        }

        // sizeof = 1, alignof = 1
        [StructLayout(LayoutKind.Sequential, Size = 1)]
        internal struct LlamaSamplerChainParams
        {
            public byte no_perf;
        }

        // sizeof = 56, alignof = 8
        [StructLayout(LayoutKind.Explicit, Size = 56)]
        internal struct LlamaBatch
        {
            [FieldOffset(0)] public int n_tokens;
            // offset 4: 4-byte padding (align pointer to 8)
            [FieldOffset(8)] public IntPtr token;
            [FieldOffset(16)] public IntPtr embd;
            [FieldOffset(24)] public IntPtr pos;
            [FieldOffset(32)] public IntPtr n_seq_id;
            [FieldOffset(40)] public IntPtr seq_id;
            [FieldOffset(48)] public IntPtr logits;
        }

        // -------------------------------------------------------------------------
        // Constants
        // -------------------------------------------------------------------------

        internal const uint LLAMA_DEFAULT_SEED = 0xFFFFFFFF;
        internal const int LLAMA_TOKEN_NULL = -1;

        // -------------------------------------------------------------------------
        // Backend
        // -------------------------------------------------------------------------
        public static bool Initialized { get; set; } = false;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_backend_init();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_backend_free();

        // -------------------------------------------------------------------------
        // Model
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi, BestFitMapping = false)]
        internal static extern IntPtr llama_model_load_from_file(
            [MarshalAs(UnmanagedType.LPStr)] string path_model,
            LlamaModelParams @params);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_model_free(IntPtr model);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_model_get_vocab(IntPtr model);

        // -------------------------------------------------------------------------
        // Context
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_init_from_model(IntPtr model, LlamaContextParams @params);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_free(IntPtr ctx);

        // -------------------------------------------------------------------------
        // Memory / KV cache
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_get_memory(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_memory_clear(IntPtr mem, byte data);

        // Removes tokens in position range [p0, p1) for the given sequence.
        // Pass p1 = -1 to remove from p0 to the end.
        // Returns false if the operation is not supported by this memory type.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool llama_memory_seq_rm(IntPtr mem, int seq_id, int p0, int p1);

        // -------------------------------------------------------------------------
        // Tokenization
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_tokenize(
            IntPtr vocab,
            IntPtr text,
            int text_len,
            IntPtr tokens,
            int n_tokens_max,
            byte add_special,
            byte parse_special);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_token_to_piece(
            IntPtr vocab,
            int token,
            IntPtr buf,
            int length,
            int lstrip,
            byte special);

        // -------------------------------------------------------------------------
        // Special vocab tokens
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_vocab_fim_pre(IntPtr vocab);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_vocab_fim_suf(IntPtr vocab);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_vocab_fim_mid(IntPtr vocab);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_vocab_fim_sep(IntPtr vocab);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool llama_vocab_is_eog(IntPtr vocab, int token);

        // -------------------------------------------------------------------------
        // Decoding
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern LlamaBatch llama_batch_get_one(IntPtr tokens, int n_tokens);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_decode(IntPtr ctx, LlamaBatch batch);

        // -------------------------------------------------------------------------
        // Sampling
        // -------------------------------------------------------------------------

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_sampler_chain_init(LlamaSamplerChainParams sparams);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_sampler_chain_add(IntPtr chain, IntPtr smpl);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int llama_sampler_sample(IntPtr smpl, IntPtr ctx, int idx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void llama_sampler_free(IntPtr smpl);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_sampler_init_min_p(float p, UIntPtr min_keep);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_sampler_init_temp(float t);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_sampler_init_top_k(int k);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_sampler_init_top_p(float p, UIntPtr min_keep);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr llama_sampler_init_dist(uint seed);

        // -------------------------------------------------------------------------
        // Native DLL loader
        // -------------------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        /// <summary>
        /// Explicitly loads all native DLLs from the extension directory using
        /// their full paths, so the OS loader never searches PATH or CWD.
        /// Must be called before any P/Invoke into llama.dll.
        /// </summary>
        internal static void LoadNativeDlls(string extDir)
        {
            // Load in dependency order: base → cpu → ggml → llama
#if DEBUG
            string[] dlls = { "ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll" };
#else
            string[] dlls = { "ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll" };
#endif
            foreach (string dll in dlls)
            {
                string fullPath = System.IO.Path.Combine(extDir, "llama.cpp", "bin", dll);
                if (LoadLibraryW(fullPath) == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(), $"LoadLibrary failed for: {fullPath}");
            }
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Tokenise <paramref name="text"/> (UTF-8) using <paramref name="vocab"/>.
        /// Returns the token ids, or an empty array on failure.
        /// </summary>
        internal static int[] Tokenize(IntPtr vocab, string text, bool addSpecial, bool parseSpecial)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<int>();

            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            unsafe
            {
                fixed (byte* textPtr = utf8)
                {
                    int n = llama_tokenize(vocab, (IntPtr)textPtr, utf8.Length,
                                           IntPtr.Zero, 0,
                                           addSpecial ? (byte)1 : (byte)0,
                                           parseSpecial ? (byte)1 : (byte)0);
                    if (n >= 0) return Array.Empty<int>(); // should be negative when buffer is null
                    n = -n;
                    var ids = new int[n];
                    fixed (int* tokPtr = ids)
                    {
                        llama_tokenize(vocab, (IntPtr)textPtr, utf8.Length,
                                       (IntPtr)tokPtr, n,
                                       addSpecial ? (byte)1 : (byte)0,
                                       parseSpecial ? (byte)1 : (byte)0);
                    }
                    return ids;
                }
            }
        }

        /// <summary>
        /// Convert a single token id to its UTF-8 string piece.
        /// Pass <paramref name="special"/>=true to get the text form of special
        /// tokens (e.g. "&lt;|file_sep|&gt;"); false returns empty for them.
        /// </summary>
        internal static string TokenToPiece(IntPtr vocab, int token, bool special = false)
        {
            byte[] buf = new byte[256];
            unsafe
            {
                fixed (byte* bufPtr = buf)
                {
                    int n = llama_token_to_piece(vocab, token, (IntPtr)bufPtr, buf.Length,
                                                 0, special ? (byte)1 : (byte)0);
                    if (n <= 0) return string.Empty;
                    return Encoding.UTF8.GetString(buf, 0, n);
                }
            }
        }

        /// <summary>Returns default model params with safe field values.</summary>
        internal static LlamaModelParams DefaultModelParams(int nGpuLayers)
        {
            return new LlamaModelParams
            {
                devices = IntPtr.Zero,
                tensor_buft_overrides = IntPtr.Zero,
                n_gpu_layers = nGpuLayers,
                split_mode = 0,            // LLAMA_SPLIT_MODE_NONE
                main_gpu = nGpuLayers <= 0 ? -1 : 0,
                tensor_split = IntPtr.Zero,
                progress_callback = IntPtr.Zero,
                progress_callback_user_data = IntPtr.Zero,
                kv_overrides = IntPtr.Zero,
                vocab_only = 0,
                use_mmap = 1,            // true
                use_direct_io = 0,
                use_mlock = 0,
                check_tensors = 0,
                use_extra_bufts = 1,            // true
                no_host = 0,
                no_alloc = 0,
            };
        }

        /// <summary>Returns default context params with safe field values.</summary>
        internal static LlamaContextParams DefaultContextParams(int nCtx, int nThreads)
        {
            return new LlamaContextParams
            {
                n_ctx = (uint)Math.Max(512, nCtx),
                n_batch = 2048,
                n_ubatch = 512,
                n_seq_max = 1,
                n_threads = nThreads,
                n_threads_batch = nThreads,
                rope_scaling_type = -1,    // LLAMA_ROPE_SCALING_TYPE_UNSPECIFIED
                pooling_type = -1,    // LLAMA_POOLING_TYPE_UNSPECIFIED
                attention_type = -1,    // LLAMA_ATTENTION_TYPE_UNSPECIFIED
                flash_attn_type = -1,    // LLAMA_FLASH_ATTN_TYPE_AUTO
                rope_freq_base = 0.0f,
                rope_freq_scale = 0.0f,
                yarn_ext_factor = -1.0f,
                yarn_attn_factor = 1.0f,
                yarn_beta_fast = 32.0f,
                yarn_beta_slow = 1.0f,
                yarn_orig_ctx = 0,
                defrag_thold = -1.0f,
                cb_eval = IntPtr.Zero,
                cb_eval_user_data = IntPtr.Zero,
                type_k = 1,     // GGML_TYPE_F16
                type_v = 1,     // GGML_TYPE_F16
                abort_callback = IntPtr.Zero,
                abort_callback_data = IntPtr.Zero,
                embeddings = 0,
                offload_kqv = 1,     // true
                no_perf = 1,     // true
                op_offload = 1,     // true
                swa_full = 1,     // true
                kv_unified = 1,     // true
                samplers = IntPtr.Zero,
                n_samplers = IntPtr.Zero,
            };
        }
    }
}
