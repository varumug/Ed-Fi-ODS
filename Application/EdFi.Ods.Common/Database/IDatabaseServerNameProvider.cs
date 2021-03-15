// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Ods.Common.Configuration;
using EdFi.Ods.Common.Context;
using NHibernate.Engine.Query;

namespace EdFi.Ods.Common.Database
{
    public interface IDatabaseServerNameProvider
    {
        string GetDatabaseServerName();
    }

    public class DefaultDatabaseServerNameProvider : IDatabaseServerNameProvider
    {
        public string GetDatabaseServerName() => null;
    }

    public class ConventionSpecificDatabaseServerNameProvider : IDatabaseServerNameProvider
    {
        private readonly ISchoolYearContextProvider _schoolYearContextProvider;
        private readonly ApiSettings _apiSettings;

        public ConventionSpecificDatabaseServerNameProvider(ISchoolYearContextProvider schoolYearContextProvider, ApiSettings apiSettings)
        {
            _schoolYearContextProvider = schoolYearContextProvider;
            _apiSettings = apiSettings;
        }

        public string GetDatabaseServerName() => $"{_apiSettings.DefaultDatabaseServerName}_{_schoolYearContextProvider.GetSchoolYear()}";
    }
}
