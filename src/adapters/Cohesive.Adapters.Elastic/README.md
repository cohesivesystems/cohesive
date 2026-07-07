# Cohesive.Adapters.Elastic

Elasticsearch query and aggregation compilers for Cohesive relation plans.

## Install

```bash
dotnet add package Cohesive.Adapters.Elastic
```

## Use When

- You want Cohesive relation queries projected to Elasticsearch requests.
- You need aggregation plans interpreted against Elasticsearch.
- You want search infrastructure to attach to Cohesive relation semantics instead of shaping application code around Elasticsearch APIs.

## Related Packages

- `Cohesive.Relations` for query and aggregation plan definitions.
- `Cohesive.Storage` for read repository abstractions.

