﻿// -----------------------------------------------------------------------
//  <copyright file="RavenDB_381.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using Raven.Abstractions.Util;
using Raven.Client.Connection;
using Raven.Client.Document;
using Raven.Client.Extensions;
using Xunit;

namespace Raven.Tests.Issues
{
	public class RavenDB_381 : RavenTest
	{
		[Fact]
		public void CanChangeConventionJustForOneType()
		{
			using(var store = NewDocumentStore())
			{
				store.Conventions.RegisterIdConvention<User>((dbName, cmds, user) => "users/" + user.Name);

				using(var session = store.OpenSession())
				{
					var entity = new User {Name = "Ayende"};
					session.Store(entity);

					Assert.Equal("users/Ayende", session.Advanced.GetDocumentId(entity));
				}
			}
		}

		[Fact]
		public void CanChangeConventionJustForOneType_Async()
		{
			using(GetNewServer())
			using (var store = new DocumentStore{Url = "http://localhost:8079"}.Initialize())
			{
				store.Conventions.RegisterAsyncIdConvention<User>((dbName, cmds, user) => new CompletedTask<string>("users/" + user.Name));

				using (var session = store.OpenAsyncSession())
				{
					var entity = new User { Name = "Ayende" };
					session.Store(entity);
					session.SaveChangesAsync().Wait();
					Assert.Equal("users/Ayende", session.Advanced.GetDocumentId(entity));
				}
			}
		}

		public class User
		{
			public string Name { get; set; }
		}
	}
}