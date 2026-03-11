using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CallGraph.Core.Search;

public sealed class BgeSmallEnV15SemanticEmbedder : ISemanticEmbedder
{
    private readonly object _loadLock = new();
    private readonly ILogger<BgeSmallEnV15SemanticEmbedder> _logger;
    private readonly LocalBgeOptions _options;

    private InferenceSession? _session;
    private ReadOnlyDictionary<string, int>? _vocab;
    private string? _inputIdsName;
    private string? _attentionMaskName;
    private string? _tokenTypeIdsName;
    private string? _outputName;
    private int _clsTokenId;
    private int _sepTokenId;
    private int _padTokenId;
    private int _unkTokenId;
    private bool _attemptedLoad;
    private bool _isAvailable;

    public BgeSmallEnV15SemanticEmbedder(
        IOptions<LocalBgeOptions> options,
        ILogger<BgeSmallEnV15SemanticEmbedder> logger)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _isAvailable;
        }
    }

    public Task<IReadOnlyList<float>> ScoreAsync(
        string queryText,
        IReadOnlyList<string> candidateTexts,
        CancellationToken cancellationToken)
    {
        if (candidateTexts.Count == 0)
            return Task.FromResult<IReadOnlyList<float>>(Array.Empty<float>());

        EnsureLoaded();
        if (!_isAvailable)
        {
            return Task.FromResult<IReadOnlyList<float>>(
                Enumerable.Repeat(0f, candidateTexts.Count).ToArray());
        }

        cancellationToken.ThrowIfCancellationRequested();

        var queryEmbedding = EmbedTexts(new[] { "query: " + queryText }, cancellationToken)[0];
        var candidateEmbeddings = EmbedTexts(candidateTexts.Select(static t => "passage: " + t).ToArray(), cancellationToken);

        var scores = new float[candidateEmbeddings.Count];
        for (var i = 0; i < candidateEmbeddings.Count; i++)
        {
            scores[i] = Dot(queryEmbedding, candidateEmbeddings[i]);
        }

        return Task.FromResult<IReadOnlyList<float>>(scores);
    }

    private void EnsureLoaded()
    {
        if (_attemptedLoad)
            return;

        lock (_loadLock)
        {
            if (_attemptedLoad)
                return;

            _attemptedLoad = true;
            if (!_options.Enabled)
            {
                _isAvailable = false;
                _logger.LogInformation("Semantic reranking disabled by configuration.");
                return;
            }

            try
            {
                var modelDirectory = ResolveModelDirectory(_options.ModelDirectory);
                var modelPath = ResolveModelPath(modelDirectory);
                var vocabPath = ResolveVocabPath(modelDirectory);

                var vocab = LoadVocab(vocabPath);

                if (!vocab.TryGetValue("[CLS]", out _clsTokenId) ||
                    !vocab.TryGetValue("[SEP]", out _sepTokenId) ||
                    !vocab.TryGetValue("[PAD]", out _padTokenId) ||
                    !vocab.TryGetValue("[UNK]", out _unkTokenId))
                {
                    throw new InvalidOperationException(
                        $"Invalid BGE vocabulary at '{vocabPath}'. Expected [CLS], [SEP], [PAD], and [UNK] tokens.");
                }

                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
                    EnableCpuMemArena = true,
                    EnableMemoryPattern = true
                };

                _session = new InferenceSession(modelPath, sessionOptions);

                _inputIdsName = ResolveInputName(_session.InputMetadata.Keys, "input_ids");
                _attentionMaskName = ResolveInputName(_session.InputMetadata.Keys, "attention_mask");
                _tokenTypeIdsName = ResolveInputName(_session.InputMetadata.Keys, "token_type_ids", required: false);
                _outputName = ResolveOutputName(_session.OutputMetadata.Keys);

                _vocab = new ReadOnlyDictionary<string, int>(vocab);
                _isAvailable = true;

                _logger.LogInformation(
                    "Loaded local BGE embedder from {ModelPath} with vocabulary {VocabPath}.",
                    modelPath,
                    vocabPath);
            }
            catch (Exception ex)
            {
                _isAvailable = false;
                _logger.LogWarning(
                    ex,
                    "Semantic reranking unavailable. Falling back to lexical-only search. Set SemanticSearch:BgeSmallEnV15:ModelDirectory to a valid local model bundle.");
            }
        }
    }

    private List<float[]> EmbedTexts(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (_session is null || _vocab is null || _inputIdsName is null || _attentionMaskName is null || _outputName is null)
            throw new InvalidOperationException("Semantic embedder is not initialized.");

        var maxSequenceLength = Math.Clamp(_options.MaxSequenceLength, 16, 512);
        var batchSize = texts.Count;

        var inputIds = new DenseTensor<long>(new[] { batchSize, maxSequenceLength });
        var attentionMask = new DenseTensor<long>(new[] { batchSize, maxSequenceLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { batchSize, maxSequenceLength });

        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encoded = EncodeText(texts[batchIndex], maxSequenceLength);
            for (var tokenIndex = 0; tokenIndex < maxSequenceLength; tokenIndex++)
            {
                inputIds[batchIndex, tokenIndex] = encoded.TokenIds[tokenIndex];
                attentionMask[batchIndex, tokenIndex] = encoded.AttentionMask[tokenIndex];
                tokenTypeIds[batchIndex, tokenIndex] = 0;
            }
        }

        var inputs = BuildInputs(inputIds, attentionMask, tokenTypeIds);
        using var results = _session.Run(inputs);

        var outputTensor = results.First(result => string.Equals(result.Name, _outputName, StringComparison.OrdinalIgnoreCase))
            .AsTensor<float>();

        return ExtractAndNormalize(outputTensor, batchSize);
    }

    private List<NamedOnnxValue> BuildInputs(
        DenseTensor<long> inputIds,
        DenseTensor<long> attentionMask,
        DenseTensor<long> tokenTypeIds)
    {
        if (_inputIdsName is null || _attentionMaskName is null)
            throw new InvalidOperationException("Semantic embedder input metadata not initialized.");

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMask)
        };

        if (!string.IsNullOrWhiteSpace(_tokenTypeIdsName))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, tokenTypeIds));
        }

        return inputs;
    }

    private List<float[]> ExtractAndNormalize(Tensor<float> outputTensor, int batchSize)
    {
        var dimensions = outputTensor.Dimensions.ToArray();
        var values = outputTensor.ToArray();

        if (dimensions.Length == 2)
        {
            var hidden = dimensions[1];
            var embeddings = new List<float[]>(batchSize);
            for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                var vector = new float[hidden];
                var offset = batchIndex * hidden;
                Array.Copy(values, offset, vector, 0, hidden);
                NormalizeInPlace(vector);
                embeddings.Add(vector);
            }

            return embeddings;
        }

        if (dimensions.Length == 3)
        {
            var sequenceLength = dimensions[1];
            var hidden = dimensions[2];
            var embeddings = new List<float[]>(batchSize);
            for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                var vector = new float[hidden];
                var offset = ((batchIndex * sequenceLength) * hidden);
                Array.Copy(values, offset, vector, 0, hidden);
                NormalizeInPlace(vector);
                embeddings.Add(vector);
            }

            return embeddings;
        }

        throw new InvalidOperationException(
            $"Unsupported ONNX output tensor rank {dimensions.Length}. Expected rank 2 or 3.");
    }

    private EncodedText EncodeText(string text, int maxSequenceLength)
    {
        if (_vocab is null)
            throw new InvalidOperationException("Semantic embedder vocabulary not initialized.");

        var tokenIds = new long[maxSequenceLength];
        var attentionMask = new long[maxSequenceLength];

        tokenIds[0] = _clsTokenId;
        attentionMask[0] = 1;
        var position = 1;

        foreach (var token in BasicTokenize(text))
        {
            var pieces = WordPieceTokenize(token);
            foreach (var pieceId in pieces)
            {
                if (position >= maxSequenceLength - 1)
                    break;

                tokenIds[position] = pieceId;
                attentionMask[position] = 1;
                position++;
            }

            if (position >= maxSequenceLength - 1)
                break;
        }

        tokenIds[position] = _sepTokenId;
        attentionMask[position] = 1;
        position++;

        for (; position < maxSequenceLength; position++)
        {
            tokenIds[position] = _padTokenId;
            attentionMask[position] = 0;
        }

        return new EncodedText(tokenIds, attentionMask);
    }

    private IEnumerable<int> WordPieceTokenize(string token)
    {
        if (_vocab is null)
            throw new InvalidOperationException("Semantic embedder vocabulary not initialized.");

        if (_vocab.TryGetValue(token, out var fullTokenId))
            return new[] { fullTokenId };

        var pieces = new List<int>();
        var start = 0;
        var isBad = false;

        while (start < token.Length)
        {
            var end = token.Length;
            int? matchedId = null;

            while (start < end)
            {
                var subToken = token[start..end];
                if (start > 0)
                    subToken = "##" + subToken;

                if (_vocab.TryGetValue(subToken, out var subTokenId))
                {
                    matchedId = subTokenId;
                    break;
                }

                end--;
            }

            if (matchedId is null)
            {
                isBad = true;
                break;
            }

            pieces.Add(matchedId.Value);
            start = end;
        }

        return isBad ? new[] { _unkTokenId } : pieces;
    }

    private static IEnumerable<string> BasicTokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var normalized = text.ToLowerInvariant();
        var tokens = new List<string>();
        var buffer = new StringBuilder();

        void FlushBuffer()
        {
            if (buffer.Length == 0)
                return;

            tokens.Add(buffer.ToString());
            buffer.Clear();
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
                continue;
            }

            FlushBuffer();

            if (!char.IsWhiteSpace(ch))
            {
                tokens.Add(ch.ToString(CultureInfo.InvariantCulture));
            }
        }

        FlushBuffer();
        return tokens;
    }

    private static void NormalizeInPlace(float[] vector)
    {
        var sumSquares = 0d;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        var magnitude = Math.Sqrt(sumSquares);
        if (magnitude <= 0)
            return;

        var scale = 1f / (float)magnitude;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }
    }

    private static float Dot(float[] left, float[] right)
    {
        if (left.Length != right.Length)
            throw new InvalidOperationException("Embedding vector dimensions do not match.");

        var sum = 0f;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }

    private static string ResolveInputName(IEnumerable<string> inputNames, string preferredName, bool required = true)
    {
        var match = inputNames.FirstOrDefault(name =>
            string.Equals(name, preferredName, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));

        if (!required && string.IsNullOrWhiteSpace(match))
            return string.Empty;

        return match ?? throw new InvalidOperationException(
            $"Unable to find ONNX input '{preferredName}'. Available inputs: {string.Join(", ", inputNames)}");
    }

    private static string ResolveOutputName(IEnumerable<string> outputNames)
    {
        var preferred = outputNames.FirstOrDefault(name =>
            string.Equals(name, "sentence_embedding", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "last_hidden_state", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("hidden_state", StringComparison.OrdinalIgnoreCase));

        return preferred ?? outputNames.First();
    }

    private static string ResolveModelDirectory(string configuredModelDirectory)
    {
        if (Path.IsPathRooted(configuredModelDirectory))
            return configuredModelDirectory;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredModelDirectory));
    }

    private static string ResolveModelPath(string modelDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(modelDirectory, "model.onnx"),
            Path.Combine(modelDirectory, "model_quantized.onnx"),
            Path.Combine(modelDirectory, "onnx", "model.onnx"),
            Path.Combine(modelDirectory, "onnx", "model_quantized.onnx")
        };

        var modelPath = candidates.FirstOrDefault(File.Exists);
        if (modelPath is null)
        {
            throw new FileNotFoundException(
                $"Could not find bge-small-en-v1.5 ONNX model under '{modelDirectory}'. Expected one of: {string.Join(", ", candidates)}");
        }

        return modelPath;
    }

    private static string ResolveVocabPath(string modelDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(modelDirectory, "vocab.txt"),
            Path.Combine(modelDirectory, "tokenizer", "vocab.txt")
        };

        var vocabPath = candidates.FirstOrDefault(File.Exists);
        if (vocabPath is null)
        {
            throw new FileNotFoundException(
                $"Could not find bge-small-en-v1.5 vocabulary under '{modelDirectory}'. Expected one of: {string.Join(", ", candidates)}");
        }

        return vocabPath;
    }

    private static Dictionary<string, int> LoadVocab(string vocabPath)
    {
        var lines = File.ReadAllLines(vocabPath);
        var vocab = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var token = lines[i].Trim();
            if (token.Length == 0)
                continue;

            vocab[token] = i;
        }

        return vocab;
    }

    private sealed record EncodedText(IReadOnlyList<long> TokenIds, IReadOnlyList<long> AttentionMask);
}
