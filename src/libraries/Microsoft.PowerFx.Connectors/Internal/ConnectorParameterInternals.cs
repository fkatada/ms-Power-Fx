﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Types;

namespace Microsoft.PowerFx.Connectors
{
    // Only contains parameters only used in HttpFunctionInvoker and determined in ConnectorFunction initialization.
    internal class ConnectorParameterInternals
    {
        // parameter and its default value, if any
        internal Dictionary<ConnectorParameter, FormulaValue> OpenApiBodyParameters { get; init; }

        internal bool SchemaLessBody { get; init; }

        internal string ContentType { get; init; }

        internal string BodySchemaReferenceId { get; init; }

        internal Dictionary<string, (bool, FormulaValue, DType)> ParameterDefaultValues { get; init; }

        internal bool SpecialBodyHandling { get; init; }
    }
}
