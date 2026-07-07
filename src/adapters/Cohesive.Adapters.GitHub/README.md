# Cohesive.Adapters.GitHub

GitHub App authentication and repository access adapters for Cohesive training/code workflows.

## Install

```bash
dotnet add package Cohesive.Adapters.GitHub
```

## Use When

- You need Cohesive code repository contracts backed by GitHub repositories.
- You want GitHub App authentication encapsulated behind Cohesive adapter interfaces.
- You are packaging source code as part of AI training or artifact workflows.

## Example

```csharp
using Cohesive.Adapters.GitHub;

services.RegisterGitHubCodeRepository(new GitHubCodeRepositorySettings
{
    AppId = configuration["GitHub:AppId"],
    KeyVaultUri = configuration["GitHub:KeyVaultUri"],
    PrivateKeySecretName = "github-app-private-key"
});
```

## Related Packages

- `Cohesive.AI` for code repository and training artifact contracts.
