// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac;
using EdFi.Common.Configuration;
using EdFi.Ods.Common.Configuration;
using EdFi.Ods.Common.Container;
using EdFi.Ods.Common.Database;

namespace EdFi.Ods.Api.Container.Modules
{
    public class YearSpecificDatabaseNameReplacementTokenProviderModule : ConditionalModule
    {
        public YearSpecificDatabaseNameReplacementTokenProviderModule(ApiSettings apiSettings)
            : base(apiSettings, nameof(YearSpecificDatabaseNameReplacementTokenProviderModule)) { }

        public override bool IsSelected() => ApiSettings.GetApiMode() == ApiMode.YearSpecific;

        public override void ApplyConfigurationSpecificRegistrations(ContainerBuilder builder)
        {
            if (!string.IsNullOrEmpty(ApiSettings.DefaultDatabaseServerName) ||
                !string.IsNullOrWhiteSpace(ApiSettings.DefaultDatabaseServerName))
            {
                builder.RegisterType<ConventionSpecificDatabaseServerNameProvider>()
                    .As<IDatabaseServerNameProvider>()
                    .SingleInstance();
            }

            builder.RegisterType<DefaultDatabaseServerNameProvider>()
                .As<IDatabaseServerNameProvider>()
                .IfNotRegistered(typeof(IDatabaseServerNameProvider))
                .SingleInstance();

            builder.RegisterType<YearSpecificDatabaseNameReplacementTokenProvider>()
                .As<IDatabaseNameReplacementTokenProvider>()
                .SingleInstance();
        }
    }
}
