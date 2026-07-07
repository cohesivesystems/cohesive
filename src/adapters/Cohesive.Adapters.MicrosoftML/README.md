# Cohesive.Adapters.MicrosoftML

Microsoft ML tokenizer integration for Cohesive text processing contracts.

## Install

```bash
dotnet add package Cohesive.Adapters.MicrosoftML
```

## Use When

- You want Cohesive tokenization contracts backed by Microsoft ML tokenizers.
- You need tokenizer behavior to remain swappable behind `Cohesive.AI` text interfaces.

## Example

```csharp
using Cohesive.Adapters.MicrosoftML;
using Cohesive.AI.Text;
using Microsoft.ML.Tokenizers;

Tokenizer microsoftTokenizer = BertTokenizer.Create("vocab.txt");
ITokenizer tokenizer = new MicrosoftMlTokenizer(microsoftTokenizer, maxTokenCount: 256);

var encoded = tokenizer.Encode("delayed shipment"u8);
```

## Related Packages

- `Cohesive.AI` for text, tokenizer, and vocabulary contracts.
