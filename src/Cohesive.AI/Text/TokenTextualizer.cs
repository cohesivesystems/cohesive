using System.Buffers;
using System.Text;

namespace Cohesive.AI.Text;

/// <summary>
/// Converts token-id spans back into normalized lexical text.
/// </summary>
public sealed class TokenTextualizer
{
    readonly ITokenVocabulary vocabulary;

    /// <summary>
    /// Creates a token textualizer.
    /// </summary>
    public TokenTextualizer(ITokenVocabulary vocabulary)
    {
        this.vocabulary = Guard.RequireNotNull(vocabulary);
    }

    /// <summary>
    /// Converts token ids into an array of strings.
    /// </summary>
    /// <param name="tokens"></param>
    /// <returns></returns>
    public IReadOnlyList<string> ToTextTokens(ReadOnlySpan<int> tokens)
    {
        var arr = new string[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            arr[i] = vocabulary.GetTokenString(tokens[i]);
        return arr;
    }

    /// <summary>
    /// Converts the token span into an array of strings based on the token table.
    /// </summary>
    /// <param name="table"></param>
    /// <param name="span"></param>
    /// <returns></returns>
    public IReadOnlyList<string> ToTextTokens(TokenTable table, TokenSpan span) =>
        ToTextTokens(table.GetSpan(span));
    
    /// <summary>
    /// Converts token ids into a whitespace-delimited lexical string.
    /// </summary>
    public string ToText(ReadOnlySpan<int> tokens)
    {
        if (tokens.IsEmpty)
            return string.Empty;

        StringBuilder builder = new(tokens.Length * 8);
        ToText(tokens, builder, delimiter: ' ');
        return builder.ToString();
    }

    /// <summary>
    /// Converts token ids into a delimited lexical string.
    /// </summary>
    /// <param name="tokens"></param>
    /// <param name="builder"></param>
    /// <param name="delimiter"></param>
    public void ToText(ReadOnlySpan<int> tokens, StringBuilder builder, char delimiter = ' ')
    {
        if (tokens.IsEmpty)
            return;
        
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
                builder.Append(delimiter);

            builder.Append(vocabulary.GetTokenString(tokens[i]));
        }
    }
    
    /// <summary>
    /// Converts token ids into a delimited lexical string.
    /// </summary>
    /// <param name="tokens"></param>
    /// <param name="buffer"></param>
    /// <param name="delimiter"></param>
    public void ToText(ReadOnlySpan<int> tokens, IBufferWriter<char> buffer, char delimiter = ' ')
    {
        if (tokens.IsEmpty)
            return;
        
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
                buffer.Write([delimiter]);

            buffer.Write(vocabulary.GetTokenString(tokens[i]));
        }
    }
}