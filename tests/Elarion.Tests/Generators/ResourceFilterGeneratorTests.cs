using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ResourceFilterGeneratorTests {
    [Fact]
    public void ResourceFilter_OwnerOnly_GuidKey_EmitsAuthorizerAndOwnerPredicate() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        source.Should().Contain(
            "partial class ContactAccess : global::Elarion.Abstractions.Authorization.IQueryAuthorizer<global::Sample.Domain.Contact>");
        source.Should().Contain("public static ContactAccess Specification { get; } = new();");
        source.Should().Contain("if (!user.IsAuthenticated)");
        source.Should().Contain("if (!global::System.Guid.TryParse(user.UserId, out var __key0))");
        source.Should().Contain("return __e => __e.OwnerId == __key0;");
    }

    [Fact]
    public void ResourceFilter_OwnerAndTenant_ComposesScopeAndGrant() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", TenantProperty = "TenantId")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // Owner is the grant (key0), tenant the scope (key1). Tenant comes from the "tenant" claim.
        source.Should().Contain(
            "global::System.Linq.Enumerable.FirstOrDefault(user.GetClaimValues(\"tenant\"))");
        // Scope AND (grant): tenant must match, and at least one grant.
        source.Should().Contain("return __e => __e.TenantId == __key1 && (__e.OwnerId == __key0);");
    }

    [Fact]
    public void ResourceFilter_StringOwner_ReadsUserIdDirectly() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Note {
                    public System.Guid Id { get; set; }
                    public string OwnerId { get; set; } = "";
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Note>(OwnerProperty = "OwnerId")]
                public sealed partial class NoteAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_NoteAccess.ResourceFilter.g.cs");

        source.Should().Contain("var __key0 = user.UserId;");
        source.Should().Contain("return __e => __e.OwnerId == __key0;");
        source.Should().NotContain("TryParse");
    }

    [Fact]
    public void ResourceFilter_IntOwner_UsesInt32TryParse() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Doc {
                    public int Id { get; set; }
                    public int OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Doc>(OwnerProperty = "OwnerId")]
                public sealed partial class DocAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_DocAccess.ResourceFilter.g.cs");

        source.Should().Contain("if (!global::System.Int32.TryParse(user.UserId, out var __key0))");
    }

    [Fact]
    public void ResourceFilter_CustomTenantClaim_IsHonored() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public string TenantId { get; set; } = "";
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(TenantProperty = "TenantId", TenantClaimType = "org")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // Tenant-only (scope), string key from the custom "org" claim, with a fail-closed null guard.
        source.Should().Contain(
            "var __key0 = global::System.Linq.Enumerable.FirstOrDefault(user.GetClaimValues(\"org\"));");
        source.Should().Contain("if (__key0 is null)");
        source.Should().Contain("return __e => __e.TenantId == __key0;");
    }

    [Fact]
    public void ResourceFilter_UnknownProperty_ReportsErrorAndGeneratesNothing() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "Missing")]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES001");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_UnsupportedPropertyType_ReportsError() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public bool OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES002");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_NullableProperty_ReportsUnsupported() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid? OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES002");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_NonPartialClass_ReportsError() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES003");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_NoRules_ReportsError() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES004");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_Shared_EmitsScopedAuthorizerWithGrantExists() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", Shared = true, ResourceTypeName = "Contact")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // Scoped service taking the grant source, plus a DI registration helper.
        source.Should()
            .Contain(
                "private readonly global::Elarion.Authorization.EntityFrameworkCore.IResourceGrantSource __grants;");
        source.Should()
            .Contain(
                "public ContactAccess(global::Elarion.Authorization.EntityFrameworkCore.IResourceGrantSource grants) => __grants = grants;");
        source.Should()
            .Contain(
                "public static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        source.Should().NotContain("public static ContactAccess Specification");

        // The owner grant is OR-combined with a correlated EXISTS over the grants table for the caller's
        // user id OR any of their roles (a small IN over Roles), matched on the resource type, id, and operation.
        source.Should().Contain("var __ok0 = global::System.Guid.TryParse(user.UserId, out var __key0);");
        source.Should().Contain("var __roles = user.Roles;");
        source.Should()
            .Contain("global::System.Linq.Queryable.Any(__grantsQuery, __g => __g.ResourceType == \"Contact\"");
        source.Should().Contain("__g.ResourceId == __e.Id.ToString()");
        source.Should().Contain("__g.Operation == __op");
        source.Should().Contain("(__g.PrincipalKind == \"user\" && __g.PrincipalId == __uid)");
        source.Should()
            .Contain(
                "(__g.PrincipalKind == \"role\" && global::System.Linq.Enumerable.Contains(__roles, __g.PrincipalId))");
        source.Should()
            .Contain(
                "return __e => (__ok0 && __e.OwnerId == __key0) || global::System.Linq.Queryable.Any(__grantsQuery,");
    }

    [Fact]
    public void ResourceFilter_SharedWithoutResourceType_ReportsElres005() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(Shared = true)]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES005");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_SharedGeneratedSource_CompilesAgainstRuntime() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", Shared = true, ResourceTypeName = "Contact")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var generated = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }
            }
            """;

        // Platform references (TPA) include the grants package, EF Core, and DI abstractions referenced by the
        // test project, so the generated shared authorizer compiles.
        CompileErrors(PlatformReferences(), entity, generated).Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_BypassPermission_LiftsTheFilterForItsHolders() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", BypassPermission = "contacts.manage", PermissionClaimType = "perm")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // After the authentication check, before any rule: a holder gets no restriction at all.
        source.Should().Contain("if (user.HasClaim(\"perm\", \"contacts.manage\"))");
        source.IndexOf("HasClaim", StringComparison.Ordinal)
            .Should().BeGreaterThan(source.IndexOf("IsAuthenticated", StringComparison.Ordinal))
            .And.BeLessThan(source.IndexOf("TryParse", StringComparison.Ordinal));
        source.Should().Contain("            return null;");
    }

    [Fact]
    public void ResourceFilter_BypassPermission_KeepsTheTenantScope() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", TenantProperty = "TenantId", BypassPermission = "contacts.manage")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // The tenant resolves first and fails closed; the bypass then lifts only the owner grant.
        source.IndexOf("GetClaimValues(\"tenant\")", StringComparison.Ordinal)
            .Should().BeLessThan(source.IndexOf("HasClaim", StringComparison.Ordinal));
        source.Should().Contain(
            """
                    if (user.HasClaim("permission", "contacts.manage"))
                    {
                        return __e => __e.TenantId == __key1;
                    }
            """);
        source.Should().NotContain("return null;");
    }

    [Fact]
    public void ResourceFilter_BypassPermission_AtRuntime_SeesEveryRowOfItsTenantOnly() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", TenantProperty = "TenantId", BypassPermission = "contacts.manage")]
                public sealed partial class ContactAccess { }
            }
            """);
        NoErrors(result);
        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }
            }
            """;
        var assembly = LoadCompiled(entity, GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs"));
        var contactType = assembly.GetType("Sample.Domain.Contact")!;
        var authorizer = assembly.GetType("Sample.Domain.ContactAccess")!.GetProperty("Specification")!.GetValue(null)!;
        var tenant = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        var admin = new ClaimsUser(Guid.CreateVersion7().ToString(), ("tenant", tenant.ToString()), ("permission", "contacts.manage"));

        object Contact(Guid tenantId) {
            var contact = Activator.CreateInstance(contactType)!;
            contactType.GetProperty("OwnerId")!.SetValue(contact, Guid.CreateVersion7());
            contactType.GetProperty("TenantId")!.SetValue(contact, tenantId);
            return contact;
        }

        var filter = (System.Linq.Expressions.LambdaExpression)authorizer.GetType().GetMethod("GetFilter")!
            .Invoke(authorizer, [admin, Elarion.Abstractions.Authorization.ResourceOperation.Read])!;
        var visible = filter.Compile();

        ((bool)visible.DynamicInvoke(Contact(tenant))!).Should().BeTrue("the bypass lifts the owner grant");
        ((bool)visible.DynamicInvoke(Contact(otherTenant))!).Should().BeFalse("the bypass never crosses the tenant");
    }

    [Fact]
    public void ResourceFilter_BypassRole_LiftsTheFilterForItsMembers_AlongsideABypassPermission() {
        var roleOnly = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", BypassRole = "Admin")]
                public sealed partial class ContactAccess { }
            }
            """);
        var both = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", BypassRole = "Ad\"min", BypassPermission = "contacts.manage")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(roleOnly);
        NoErrors(both);
        var roleSource = GetGeneratedSource(roleOnly, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");
        roleSource.Should().Contain("if (user.IsInRole(\"Admin\"))").And.Contain("            return null;");
        roleSource.IndexOf("IsInRole", StringComparison.Ordinal)
            .Should().BeGreaterThan(roleSource.IndexOf("IsAuthenticated", StringComparison.Ordinal));
        // Either one bypasses; the role name is emitted as an escaped literal.
        GetGeneratedSource(both, "Sample_Domain_ContactAccess.ResourceFilter.g.cs").Should().Contain(
            "if (user.HasClaim(\"permission\", \"contacts.manage\") || user.IsInRole(\"Ad\\\"min\"))");
    }

    [Fact]
    public void ResourceFilter_BypassRole_AtRuntime_SeesEveryRowOfItsTenantOnly() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", TenantProperty = "TenantId", BypassRole = "Admin")]
                public sealed partial class ContactAccess { }
            }
            """);
        NoErrors(result);
        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }
            }
            """;
        var assembly = LoadCompiled(entity, GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs"));
        var contactType = assembly.GetType("Sample.Domain.Contact")!;
        var authorizer = assembly.GetType("Sample.Domain.ContactAccess")!.GetProperty("Specification")!.GetValue(null)!;
        var tenant = Guid.CreateVersion7();
        var admin = new ClaimsUser(Guid.CreateVersion7().ToString(), ("tenant", tenant.ToString())) { Roles = ["Admin"] };
        var staff = new ClaimsUser(Guid.CreateVersion7().ToString(), ("tenant", tenant.ToString())) { Roles = ["Staff"] };
        var adminWithoutTenant = new ClaimsUser(Guid.CreateVersion7().ToString()) { Roles = ["Admin"] };

        object Contact(Guid tenantId) {
            var contact = Activator.CreateInstance(contactType)!;
            contactType.GetProperty("OwnerId")!.SetValue(contact, Guid.CreateVersion7());
            contactType.GetProperty("TenantId")!.SetValue(contact, tenantId);
            return contact;
        }

        bool Sees(ClaimsUser user, object contact) {
            var filter = (System.Linq.Expressions.LambdaExpression)authorizer.GetType().GetMethod("GetFilter")!
                .Invoke(authorizer, [user, Elarion.Abstractions.Authorization.ResourceOperation.Read])!;
            return (bool)filter.Compile().DynamicInvoke(contact)!;
        }

        Sees(admin, Contact(tenant)).Should().BeTrue("the bypass lifts the owner grant");
        Sees(admin, Contact(Guid.CreateVersion7())).Should().BeFalse("the bypass never crosses the tenant");
        Sees(staff, Contact(tenant)).Should().BeFalse("another role does not bypass");
        Sees(adminWithoutTenant, Contact(tenant)).Should().BeFalse("a scope that cannot be resolved fails closed");
    }

    [Fact]
    public void ResourceFilter_PrincipalRelation_AtRuntime_AgreesWithTheFilter() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", BypassPermission = "contacts.manage", BypassRole = "Admin")]
                public sealed partial class ContactAccess { }
            }
            """);
        NoErrors(result);
        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }
            }
            """;
        var assembly = LoadCompiled(entity, GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs"));
        var contactType = assembly.GetType("Sample.Domain.Contact")!;
        var authorizer = assembly.GetType("Sample.Domain.ContactAccess")!.GetProperty("Specification")!.GetValue(null)!;
        var owner = new ClaimsUser(Guid.CreateVersion7().ToString());
        var manager = new ClaimsUser(Guid.CreateVersion7().ToString(), ("permission", "contacts.manage"));
        var admin = new ClaimsUser(Guid.CreateVersion7().ToString()) { Roles = ["Admin"] };
        var outsider = new ClaimsUser(Guid.CreateVersion7().ToString(), ("permission", "contacts.read")) { Roles = ["Staff"] };
        ClaimsUser[] users = [owner, manager, admin, outsider];
        var contact = Activator.CreateInstance(contactType)!;
        contactType.GetProperty("OwnerId")!.SetValue(contact, Guid.Parse(owner.UserId));

        var filterMethod = authorizer.GetType().GetMethod("GetFilter")!;
        var relation = (System.Linq.Expressions.LambdaExpression)authorizer.GetType().GetMethod("GetPrincipalRelation")!
            .MakeGenericMethod(typeof(Guid))
            .Invoke(authorizer, [new ClaimsDirectory<Guid>(users, Guid.Parse), Elarion.Abstractions.Authorization.ResourceOperation.Read])!;
        var admits = relation.Compile();

        foreach (var user in users) {
            var filter = (System.Linq.Expressions.LambdaExpression?)filterMethod
                .Invoke(authorizer, [user, Elarion.Abstractions.Authorization.ResourceOperation.Read]);
            var visible = filter is null || (bool)filter.Compile().DynamicInvoke(contact)!;

            ((bool)admits.DynamicInvoke(contact, Guid.Parse(user.UserId))!).Should().Be(visible);
        }

        ((bool)admits.DynamicInvoke(contact, Guid.Parse(outsider.UserId))!).Should().BeFalse();
        ((bool)admits.DynamicInvoke(contact, Guid.Parse(admin.UserId))!).Should().BeTrue();
    }

    [Fact]
    public void ResourceFilter_PrincipalRelation_IsRefusedForATenantScope_AndForAnotherIdType() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", TenantProperty = "TenantId")]
                public sealed partial class ScopedAccess { }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class OwnerAccess { }
            }
            """);
        NoErrors(result);
        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }
            }
            """;
        var assembly = LoadCompiled(
            entity,
            GetGeneratedSource(result, "Sample_Domain_ScopedAccess.ResourceFilter.g.cs"),
            GetGeneratedSource(result, "Sample_Domain_OwnerAccess.ResourceFilter.g.cs"));

        Action Ask(string type, Type keyType, object directory) {
            var authorizer = assembly.GetType(type)!.GetProperty("Specification")!.GetValue(null)!;
            return () => authorizer.GetType().GetMethod("GetPrincipalRelation")!.MakeGenericMethod(keyType)
                .Invoke(authorizer, [directory, Elarion.Abstractions.Authorization.ResourceOperation.Read]);
        }

        // A relation that dropped the tenant scope would list principals of every tenant: refused instead.
        Ask("Sample.Domain.ScopedAccess", typeof(Guid), new ClaimsDirectory<Guid>([], Guid.Parse))
            .Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<NotSupportedException>().WithMessage("*tenant claim (TenantId)*");
        Ask("Sample.Domain.OwnerAccess", typeof(long), new ClaimsDirectory<long>([], long.Parse))
            .Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<NotSupportedException>().WithMessage("*Guid principal ids*Int64 ids*");
    }

    [Fact]
    public void ResourceFilter_Shared_EmitsTheGrantRelationForUsersAndRoles() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", Shared = true, ResourceTypeName = "Contact")]
                public sealed partial class ContactAccess { }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(Shared = true, ResourceTypeName = "Contact")]
                public sealed partial class SharedOnlyAccess { }
            }
            """);
        NoErrors(result);
        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }
            }
            """;
        var owned = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");
        var sharedOnly = GetGeneratedSource(result, "Sample_Domain_SharedOnlyAccess.ResourceFilter.g.cs");

        owned.Should().Contain("global::Elarion.Abstractions.Authorization.IPrincipalQueryAuthorizer<global::Sample.Domain.Contact>")
            .And.Contain("return (__e, __p) => __e.OwnerId == __p || global::System.Linq.Queryable.Any(__grantsQuery,")
            .And.Contain("(__g.PrincipalKind == \"user\" && __g.PrincipalId == __p.ToString())")
            .And.Contain("(__g.PrincipalKind == \"role\" && global::System.Linq.Queryable.Any(__rolesQuery, __r => __r.PrincipalId == __p && __r.Role == __g.PrincipalId))")
            .And.Contain("__g.Operation == __op");
        // The owner column fixes the id type; without one the grants' textual principal id accepts every supported type.
        owned.Should().Contain("__PrincipalRelation_guid(").And.NotContain("__PrincipalRelation_string(");
        sharedOnly.Should().Contain("__PrincipalRelation_guid(").And.Contain("__PrincipalRelation_string(")
            .And.Contain("__PrincipalRelation_int(").And.Contain("__PrincipalRelation_long(")
            .And.Contain("__g.PrincipalId == __p)");
        CompileErrors(PlatformReferences(), entity, owned, sharedOnly).Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_GeneratedSource_CompilesAgainstRuntime() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(OwnerProperty = "OwnerId", TenantProperty = "TenantId")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var generated = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }
            }
            """;

        CompileErrors(entity, generated).Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_ReusesOutputsAfterIrrelevantEdit() {
        var source =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Abstractions.Authorization.ResourceFilter<Contact>(
                    OwnerProperty = "OwnerId", BypassPermission = "contacts.manage")]
                public sealed partial class ContactAccess { }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ResourceFilterGenerator(),
            source,
            "ResourceFilterTargets");
    }

    private static GeneratorDriverRunResult Generate(string testSource) {
        var source =
            $$"""
              {{testSource}}
              """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ResourceFilterGeneratorTests",
            [syntaxTree],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ResourceFilterGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(params string[] sources) {
        return CompileErrors(RuntimeReferences(), sources);
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(IReadOnlyList<MetadataReference> references,
        params string[] sources) {
        var compilation = CSharpCompilation.Create(
            "ResourceFilterCompileCheck",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Preview))),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static System.Reflection.Assembly LoadCompiled(params string[] sources) {
        var compilation = CSharpCompilation.Create(
            $"ResourceFilterRuntime{Guid.CreateVersion7():N}",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Preview))),
            RuntimeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        return System.Reflection.Assembly.Load(stream.ToArray());
    }

    private sealed class ClaimsUser(string userId, params (string Type, string Value)[] claims)
        : Elarion.Abstractions.Identity.ICurrentUser {
        public string UserId => userId;

        public IReadOnlyList<(string Type, string Value)> AllClaims => claims;

        public string? Email => null;

        public IReadOnlyList<string> Roles { get; init; } = [];

        public bool IsAuthenticated => true;

        public bool IsInRole(string role) => Roles.Contains(role);

        public bool HasClaim(string type, string value) => claims.Contains((type, value));

        public IEnumerable<string> GetClaimValues(string type) =>
            claims.Where(claim => claim.Type == type).Select(claim => claim.Value);
    }

    private sealed class ClaimsDirectory<TKey>(IReadOnlyList<ClaimsUser> users, Func<string, TKey> parse)
        : Elarion.Abstractions.Authorization.IPrincipalDirectory<TKey>
        where TKey : notnull {
        public IQueryable<Elarion.Abstractions.Authorization.PrincipalRole<TKey>> Roles => users
            .SelectMany(user => user.Roles.Select(role => new Elarion.Abstractions.Authorization.PrincipalRole<TKey> {
                PrincipalId = parse(user.UserId), Role = role
            }))
            .AsQueryable();

        public IQueryable<Elarion.Abstractions.Authorization.PrincipalClaim<TKey>> Claims => users
            .SelectMany(user => user.AllClaims.Select(claim => new Elarion.Abstractions.Authorization.PrincipalClaim<TKey> {
                PrincipalId = parse(user.UserId), Type = claim.Type, Value = claim.Value
            }))
            .AsQueryable();
    }

    private static void NoErrors(GeneratorDriverRunResult result) {
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => SharedMetadataReferences.FromFile(path))
            .ToArray();
    }

    private static IReadOnlyList<MetadataReference> RuntimeReferences() {
        var references = PlatformReferences().ToList();
        references.Add(SharedMetadataReferences.FromFile(
            typeof(Elarion.Abstractions.Authorization.IQueryAuthorizer<>).Assembly.Location));
        return references;
    }
}
