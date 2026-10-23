using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Serializes shared-instance Platform DDL across test classes without holding a production process lock.</summary>
[CollectionDefinition("PlatformPersistence", DisableParallelization = true)]
public sealed class PlatformPersistenceCollection;
