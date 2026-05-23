namespace Recall.Ingestion;

/// <summary>
/// Sliding-window chunker that splits text on word boundaries.
/// Uses the heuristic: 1 token ≈ 4 characters (suitable for English/Latin scripts).
/// </summary>
public static class Chunker
{
    private const int MinTokens = 50;

    /// <summary>
    /// Split <paramref name="text"/> into overlapping chunks.
    /// </summary>
    /// <param name="text">Source text (extracted by IFilter).</param>
    /// <param name="chunkSize">Target chunk size in tokens.</param>
    /// <param name="overlap">Number of tokens from the end of each chunk to prepend to the next.</param>
    public static IEnumerable<string> Chunk(string text, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        // Split into words, preserving punctuation as part of words
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        // Convert token targets to approximate word counts.
        // Average English word ≈ 5 chars; 1 token ≈ 4 chars → ~0.8 words per token
        // Simpler: track cumulative char length and convert with the /4 heuristic.
        int chunkChars   = chunkSize * 4;
        int overlapChars = overlap   * 4;

        int startWord = 0;

        while (startWord < words.Length)
        {
            // Build one chunk: accumulate words until we hit chunkChars
            int charCount = 0;
            int endWord   = startWord;

            while (endWord < words.Length && charCount < chunkChars)
            {
                charCount += words[endWord].Length + 1; // +1 for space
                endWord++;
            }

            // endWord is now exclusive — the chunk is words[startWord..endWord]
            if (endWord == startWord) break; // safety: no progress

            var chunkWords = new ArraySegment<string>(words, startWord, endWord - startWord);
            var chunk      = string.Join(' ', chunkWords);

            // Only emit chunks that meet the minimum token threshold
            int tokenEstimate = chunk.Length / 4;
            if (tokenEstimate >= MinTokens)
                yield return chunk;

            // Advance start: skip forward by (chunkSize - overlap) tokens worth of words,
            // i.e. stay back 'overlapChars' worth of content for the next chunk.
            int advanceChars = chunkChars - overlapChars;
            if (advanceChars <= 0) advanceChars = chunkChars; // guard against bad config

            int walked = 0;
            int nextStart = startWord;
            while (nextStart < endWord && walked < advanceChars)
            {
                walked += words[nextStart].Length + 1;
                nextStart++;
            }

            // If we haven't moved at all, force at least one word of progress
            if (nextStart == startWord) nextStart = startWord + 1;

            startWord = nextStart;
        }
    }
}
