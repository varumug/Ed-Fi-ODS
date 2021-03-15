// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Common.Extensions;
using Microsoft.Extensions.Configuration;

namespace EdFi.Ods.Common.Database
{
    /// <summary>
    /// Gets the connection string using a configured named connection string as a prototype for the connection string
    /// with an injected <see cref="IDatabaseNameReplacementTokenProvider"/> to replace token in database name.
    /// </summary>
    public class PrototypeTokenReplacementConnectionStringProvider : IOdsDatabaseConnectionStringProvider
    {
        private readonly IConfiguration _configuration;
        private readonly IDatabaseNameReplacementTokenProvider _databaseNameReplacementTokenProvider;
        private readonly IDatabaseServerNameProvider _databaseServerNameProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrototypeTokenReplacementConnectionStringProvider"/> class using
        /// the specified "prototype" named connection string from the application configuration file and the supplied database name replacement token provider.
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="databaseNameReplacementTokenProvider">The provider that builds the database name replacement token for use in the resulting connection string.</param>
        /// <param name="databaseServerNameProvider"></param>
        public PrototypeTokenReplacementConnectionStringProvider(IConfiguration configuration,
            IDatabaseNameReplacementTokenProvider databaseNameReplacementTokenProvider,
            IDatabaseServerNameProvider databaseServerNameProvider)
        {
            _databaseNameReplacementTokenProvider = databaseNameReplacementTokenProvider;
            _databaseServerNameProvider = databaseServerNameProvider;
            _configuration = configuration;
        }

        /// <summary>
        /// Gets the connection string using a configured named connection string with the database replaced using the specified database name replacement token provider.
        /// </summary>
        /// <returns>The connection string.</returns>
        public string GetConnectionString()
        {
            string protoTypeConnectionString = _configuration.GetConnectionString("EdFi_Ods");

            return protoTypeConnectionString.IsFormatString()
                ? string.Format(
                    protoTypeConnectionString, _databaseNameReplacementTokenProvider.GetReplacementToken(),
                    _databaseServerNameProvider.GetDatabaseServerName())
                : protoTypeConnectionString;
        }
    }
}
