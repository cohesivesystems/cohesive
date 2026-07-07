# Cohesive.Identity

Identity context, scope resolution, and in-memory identity directory helpers for Cohesive applications.

## Install

```bash
dotnet add package Cohesive.Identity
```

## Use When

- You need to resolve operation context, principal identity, and scope references in a Cohesive application.
- You want a small in-memory identity directory for local development, tests, or bootstrap flows.
- You need identity-aware API or storage adapters to share the same operation context model.

## Example

```csharp
using Cohesive.Identity;

var directory = InMemoryIdentityDomainRepositoryFactory
    .Create(new InMemoryIdentityDirectoryBuilder()
        .AddScope(new("tenant-a", "tenant", "Tenant A"))
        .AddPrincipal(new("user:alice", PrincipalKind.User, Email: "alice@example.com"))
        .AddScopeGrant("user:alice", "tenant-a", "tenant", ["orders.read"], isDefaultScope: true)
        .Build())
    .CreateDirectory();

var principal = await directory.FindPrincipalAsync(new(Email: "alice@example.com"));
```

## Related Packages

- `Cohesive.Adapters.AspNet` for request identity and API scope policy enforcement.
- `Cohesive.Api` for scope-aware API declarations.
