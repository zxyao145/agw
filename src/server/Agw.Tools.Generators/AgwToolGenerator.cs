using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Agw.Tools.Generators;

[Generator]
public sealed class AgwToolGenerator : IIncrementalGenerator
{
    private const string ToolAbstractionsNamespace = "Agw.Tools.Abstractions.";
    private const string ToolAttribute = ToolAbstractionsNamespace + "Attributes.AgwToolAttribute";
    private const string ContainerAttribute = ToolAbstractionsNamespace + "Attributes.AgwToolContainerAttribute";
    private const string IgnoreAttribute = ToolAbstractionsNamespace + "Attributes.AgwToolIgnoreAttribute";
    private const string ServiceAttribute = ToolAbstractionsNamespace + "Attributes.AgwToolServiceAttribute";
    private const string FromServicesAttribute = "Microsoft.AspNetCore.Mvc.FromServicesAttribute";
    private const string ExcludeAttribute = ToolAbstractionsNamespace + "Attributes.ExcludeToolFromListAttribute";
    private const string WorkspaceAttribute =
        ToolAbstractionsNamespace + "Attributes.AgwToolRequiresWorkspaceAttribute";
    private const string SchemaAttribute = ToolAbstractionsNamespace + "Attributes.AgwToolParameterSchemaAttribute";
    private const string DescriptionAttribute = "System.ComponentModel.DescriptionAttribute";
    private const string ToolSetInterface = ToolAbstractionsNamespace + "Generated.IAgwToolSet<TToolContainer>";
    private const string ToolBlockDeclarationInterface =
        ToolAbstractionsNamespace + "Generated.IAgwToolBlockDeclaration";

    private static readonly DiagnosticDescriptor UnsupportedSignature = new(
        "AGWTOOL001",
        "Unsupported tool signature",
        "Tool method '{0}' cannot be generated: {1}",
        "Agw.Tools",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "AGWTOOL002",
        "Invalid tool declaration",
        "Tool declaration '{0}' is invalid: {1}",
        "Agw.Tools",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> containers = context.SyntaxProvider.ForAttributeWithMetadataName(
            ContainerAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol
        );
        IncrementalValuesProvider<INamedTypeSymbol> methodTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            ToolAttribute,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, _) => ctx.TargetSymbol.ContainingType
        );
        IncrementalValuesProvider<INamedTypeSymbol> toolSetOwners = context
            .SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node)
            )
            .Where(static type => type != null && GetToolSetTypes(type).Count > 0)
            .Select(static (type, _) => type!);

        context.RegisterSourceOutput(
            containers
                .Collect()
                .Combine(methodTypes.Collect())
                .Combine(toolSetOwners.Collect())
                .Combine(context.CompilationProvider),
            static (productionContext, source) =>
                Generate(
                    productionContext,
                    source.Left.Left.Left,
                    source.Left.Left.Right,
                    source.Left.Right,
                    source.Right
                )
        );
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<INamedTypeSymbol> containers,
        ImmutableArray<INamedTypeSymbol> methodTypes,
        ImmutableArray<INamedTypeSymbol> toolSetOwners,
        Compilation compilation
    )
    {
        INamedTypeSymbol[] types = containers
            .Concat(methodTypes)
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OfType<INamedTypeSymbol>()
            .ToArray();
        if (types.Length == 0 && toolSetOwners.Length == 0)
        {
            return;
        }

        var tools = new List<ToolModel>();
        var names = new Dictionary<string, IMethodSymbol>(StringComparer.OrdinalIgnoreCase);
        var obsoleteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (INamedTypeSymbol type in types)
        {
            AttributeData? container = GetAttribute(type, ContainerAttribute);
            IEnumerable<IMethodSymbol> methods = type.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(static method =>
                    method.MethodKind == MethodKind.Ordinary && method.DeclaredAccessibility == Accessibility.Public
                )
                .Where(method => container != null || GetAttribute(method, ToolAttribute) != null);

            foreach (IMethodSymbol method in methods)
            {
                if (GetAttribute(method, IgnoreAttribute) != null)
                {
                    continue;
                }

                ToolModel? model = CreateToolModel(context, type, method, container, compilation);
                if (model == null)
                {
                    continue;
                }

                if (
                    GetAttribute(method, "System.ObsoleteAttribute") != null
                    || GetAttribute(type, "System.ObsoleteAttribute") != null
                )
                {
                    obsoleteNames.Add(model.Name);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    Report(
                        context,
                        InvalidDeclaration,
                        method,
                        method.ToDisplayString(),
                        "the generated name is empty"
                    );
                    continue;
                }
                if (names.TryGetValue(model.Name, out IMethodSymbol? duplicate))
                {
                    Report(
                        context,
                        InvalidDeclaration,
                        method,
                        method.ToDisplayString(),
                        $"name '{model.Name}' is also used by '{duplicate.ToDisplayString()}'"
                    );
                    continue;
                }

                names.Add(model.Name, method);
                tools.Add(model);
            }
        }

        ToolSetOwnerModel[] toolSets = toolSetOwners
            .Distinct(SymbolEqualityComparer.Default)
            .OfType<INamedTypeSymbol>()
            .Select(type => CreateToolSetOwnerModel(context, type, tools, compilation.Assembly))
            .Where(static model => model != null)
            .Cast<ToolSetOwnerModel>()
            .ToArray();
        foreach (ToolSetOwnerModel toolSet in toolSets)
        {
            context.AddSource(
                SanitizeHintName(toolSet.Type.ToDisplayString()) + ".AgwToolSet.g.cs",
                SourceText.From(RenderToolSetOwner(toolSet), Encoding.UTF8)
            );
        }

        if (context.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        string generatedNamespace = BuildGeneratedNamespace(compilation.AssemblyName ?? "Assembly");
        context.AddSource(
            "AgwToolModule.g.cs",
            SourceText.From(RenderModule(generatedNamespace, tools, toolSets, obsoleteNames), Encoding.UTF8)
        );
    }

    private static ToolModel? CreateToolModel(
        SourceProductionContext context,
        INamedTypeSymbol type,
        IMethodSymbol method,
        AttributeData? container,
        Compilation compilation
    )
    {
        AttributeData? tool = GetAttribute(method, ToolAttribute);
        string? explicitName = GetNamedString(tool, "Name") ?? GetConstructorString(tool, 0);
        if (
            tool?.ConstructorArguments.Length > 0
            && tool.ConstructorArguments[0].Type?.SpecialType != SpecialType.System_String
        )
        {
            explicitName = null;
        }
        string name = explicitName ?? TrimAsyncSuffix(method.Name);

        int? permission = GetToolPermission(tool);
        permission ??= GetConstructorInt(container, 0);
        if (permission == null)
        {
            Report(
                context,
                InvalidDeclaration,
                method,
                method.ToDisplayString(),
                "AgwToolPermission is not declared on the method or container"
            );
            return null;
        }

        if (method.IsGenericMethod || type.IsGenericType)
        {
            Report(
                context,
                UnsupportedSignature,
                method,
                method.ToDisplayString(),
                "generic tool methods and generic containing types are not supported"
            );
            return null;
        }
        if (
            method.ReturnsByRef
            || method.ReturnsByRefReadonly
            || method.IsAsync && method.ReturnType.SpecialType == SpecialType.System_Void
        )
        {
            Report(
                context,
                UnsupportedSignature,
                method,
                method.ToDisplayString(),
                "by-ref and async void returns are not supported"
            );
            return null;
        }
        if (method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            Report(
                context,
                UnsupportedSignature,
                method,
                method.ToDisplayString(),
                "ref, in, and out parameters are not supported"
            );
            return null;
        }

        string category = GetNamedString(tool, "Category") ?? GetNamedString(container, "DefaultCategory") ?? "General";
        bool allowInPlanMode =
            GetNamedBool(tool, "AllowInPlanMode") ?? GetNamedBool(container, "AllowInPlanMode") ?? false;
        int timeoutMs = GetNamedInt(tool, "TimeoutMs") ?? 5000;
        bool excluded = HasAttribute(method, ExcludeAttribute) || HasAttribute(type, ExcludeAttribute);
        bool requiresWorkspace = HasAttribute(type, WorkspaceAttribute);
        string description = GetDescription(method);
        string displayName = GetDescriptionAttribute(method, "System.ComponentModel.DisplayNameAttribute") ?? name;
        bool isAsync = IsTaskLike(method.ReturnType, out ITypeSymbol? resultType);
        ITypeSymbol? effectiveReturnType = isAsync ? resultType : method.ReturnType;

        var parameters = new List<ParameterModel>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            bool cancellation = parameter.Type.ToDisplayString() == "System.Threading.CancellationToken";
            bool agwService = HasAttribute(parameter, ServiceAttribute);
            bool fromServices = HasAttribute(parameter, FromServicesAttribute);
            if (agwService && fromServices)
            {
                Report(
                    context,
                    InvalidDeclaration,
                    parameter,
                    method.ToDisplayString(),
                    $"parameter '{parameter.Name}' cannot declare both AgwToolServiceAttribute and FromServicesAttribute"
                );
                return null;
            }
            bool service = agwService || fromServices;
            if (
                !cancellation
                && !service
                && !IsSupportedType(parameter.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))
            )
            {
                Report(
                    context,
                    UnsupportedSignature,
                    parameter,
                    method.ToDisplayString(),
                    $"parameter '{parameter.Name}' has unsupported type '{parameter.Type.ToDisplayString()}'"
                );
                return null;
            }
            parameters.Add(CreateParameter(parameter, service, cancellation));
        }

        if (
            effectiveReturnType != null
            && effectiveReturnType.SpecialType != SpecialType.System_Void
            && !IsSupportedType(effectiveReturnType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default))
        )
        {
            Report(
                context,
                UnsupportedSignature,
                method,
                method.ToDisplayString(),
                $"return type '{effectiveReturnType.ToDisplayString()}' is not supported"
            );
            return null;
        }

        string inputSchema = BuildInputSchema(parameters);
        string? returnSchema =
            effectiveReturnType == null || effectiveReturnType.SpecialType == SpecialType.System_Void
                ? null
                : BuildTypeSchema(effectiveReturnType, null, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

        return new ToolModel(
            type,
            method,
            name,
            displayName,
            description,
            category,
            permission.Value,
            allowInPlanMode,
            excluded,
            requiresWorkspace,
            timeoutMs,
            isAsync,
            inputSchema,
            returnSchema,
            parameters
        );
    }

    private static ParameterModel CreateParameter(IParameterSymbol parameter, bool service, bool cancellation)
    {
        AttributeData? schema = GetAttribute(parameter, SchemaAttribute);
        string? description = GetDescription(parameter);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = GetParameterDescription(parameter);
        }
        string? schemaType = GetNamedString(schema, "Type");
        string? format = GetNamedString(schema, "Format");
        string[]? enumValues = GetNamedString(schema, "EnumValues")
            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Trim())
            .ToArray();
        return new ParameterModel(
            parameter,
            description,
            service,
            cancellation,
            parameter.HasExplicitDefaultValue,
            parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue : null,
            schemaType,
            format,
            enumValues,
            schema
        );
    }

    private static ToolSetOwnerModel? CreateToolSetOwnerModel(
        SourceProductionContext context,
        INamedTypeSymbol type,
        IReadOnlyList<ToolModel> tools,
        IAssemblySymbol assembly
    )
    {
        bool isToolBlock = HasInterface(type, ToolBlockDeclarationInterface);
        if (
            type.TypeKind != TypeKind.Class
            || type.IsStatic
            || type.IsAbstract
            || type.IsGenericType
            || type.ContainingType != null
            || type.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal
            || type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is RecordDeclarationSyntax)
        )
        {
            Report(
                context,
                InvalidDeclaration,
                type,
                type.ToDisplayString(),
                "generated Tool set owners must be top-level, concrete, non-generic public or internal classes"
            );
            return null;
        }
        bool isPartial = type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
        );
        if (!isPartial)
        {
            Report(
                context,
                InvalidDeclaration,
                type,
                type.ToDisplayString(),
                $"types using {ToolSetInterface} must be declared partial"
            );
            return null;
        }
        if (type.GetMembers("ToolTypes").Length > 0 || type.GetMembers("CreateDescriptor").Length > 0)
        {
            Report(
                context,
                InvalidDeclaration,
                type,
                type.ToDisplayString(),
                "ToolTypes and CreateDescriptor are reserved for generated members"
            );
            return null;
        }
        if (
            isToolBlock
            && !type.InstanceConstructors.Any(static constructor =>
                constructor.Parameters.Length == 0
                && constructor.DeclaredAccessibility
                    is Accessibility.Public
                        or Accessibility.Internal
                        or Accessibility.ProtectedOrInternal
            )
        )
        {
            Report(
                context,
                InvalidDeclaration,
                type,
                type.ToDisplayString(),
                "generated ToolBlock declarations require an accessible parameterless constructor"
            );
            return null;
        }

        IReadOnlyList<INamedTypeSymbol> toolTypes = GetToolSetTypes(type);
        foreach (
            INamedTypeSymbol toolType in toolTypes.Where(toolType =>
                SymbolEqualityComparer.Default.Equals(toolType.ContainingAssembly, assembly)
                && !tools.Any(tool => SymbolEqualityComparer.Default.Equals(tool.Type, toolType))
            )
        )
        {
            Report(
                context,
                InvalidDeclaration,
                type,
                type.ToDisplayString(),
                $"Tool container '{toolType.ToDisplayString()}' does not produce any generated Tools"
            );
        }

        return new ToolSetOwnerModel(type, toolTypes, isToolBlock);
    }

    private static string RenderToolSetOwner(ToolSetOwnerModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        if (!model.Type.ContainingNamespace.IsGlobalNamespace)
        {
            builder.Append("namespace ").Append(model.Type.ContainingNamespace.ToDisplayString()).AppendLine(";");
            builder.AppendLine();
        }
        builder
            .Append(GetAccessibility(model.Type.DeclaredAccessibility))
            .Append(" partial class ")
            .Append(EscapeIdentifier(model.Type.Name));
        if (model.IsToolBlock)
        {
            builder.Append(" : global::Agw.Tools.Abstractions.Generated.IAgwGeneratedToolBlockRegistration");
        }
        builder.AppendLine();
        builder.AppendLine("{");
        builder.AppendLine(
            "    public global::System.Collections.Generic.IReadOnlyList<global::System.Type> ToolTypes { get; } = new global::System.Type[]"
        );
        builder.AppendLine("    {");
        foreach (INamedTypeSymbol toolType in model.ToolTypes)
        {
            builder.Append("        typeof(").Append(FullyQualified(toolType)).AppendLine("),");
        }
        builder.AppendLine("    };");
        if (model.IsToolBlock)
        {
            builder.AppendLine();
            builder.AppendLine(
                "    public global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolBlockDescriptor CreateDescriptor(global::Agw.Tools.Abstractions.Generated.IAgwGeneratedToolLookup lookup)"
            );
            builder.AppendLine("    {");
            builder.AppendLine(
                "        var declaration = (global::Agw.Tools.Abstractions.Generated.IAgwToolBlockDeclaration)this;"
            );
            builder.AppendLine("        var memberToolNames = new global::System.Collections.Generic.List<string>();");
            builder.AppendLine("        foreach (global::System.Type toolType in ToolTypes)");
            builder.AppendLine("        {");
            builder.AppendLine(
                "            global::System.Collections.Generic.IReadOnlyList<global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolDescriptor> tools = lookup.GetTools(toolType);"
            );
            builder.AppendLine("            if (tools.Count == 0)");
            builder.AppendLine("            {");
            builder.AppendLine(
                "                throw new global::System.InvalidOperationException(\"Generated Tool container '\" + toolType.FullName + \"' is not registered.\");"
            );
            builder.AppendLine("            }");
            builder.AppendLine(
                "            foreach (global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolDescriptor tool in tools)"
            );
            builder.AppendLine("            {");
            builder.AppendLine("                memberToolNames.Add(tool.Name);");
            builder.AppendLine("            }");
            builder.AppendLine("        }");
            builder.AppendLine(
                "        return new global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolBlockDescriptor(declaration.Name, declaration.DisplayName, declaration.Description, declaration.Scopes, declaration.ExcludeFromList, memberToolNames);"
            );
            builder.AppendLine("    }");
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string RenderModule(
        string generatedNamespace,
        IReadOnlyList<ToolModel> tools,
        IReadOnlyList<ToolSetOwnerModel> toolSets,
        IEnumerable<string> obsoleteNames
    )
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.Append("namespace ").Append(generatedNamespace).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine(
            "public sealed class AgwToolModule : global::Agw.Tools.Abstractions.Generated.IAgwGeneratedToolModule"
        );
        builder.AppendLine("{");
        builder.AppendLine("    public static AgwToolModule Instance { get; } = new AgwToolModule();");
        builder.AppendLine("    private AgwToolModule() { }");
        builder.AppendLine(
            "    public global::System.Collections.Generic.IReadOnlyList<global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolDescriptor> Tools { get; } = CreateTools();"
        );
        builder.AppendLine(
            "    public global::System.Collections.Generic.IReadOnlyList<global::Agw.Tools.Abstractions.Generated.IAgwGeneratedToolBlockRegistration> ToolBlockRegistrations { get; } = CreateToolBlockRegistrations();"
        );
        builder.Append(
            "    public global::System.Collections.Generic.IReadOnlyList<string> ObsoleteToolNames { get; } = new string[] { "
        );
        builder.Append(string.Join(", ", obsoleteNames.OrderBy(static name => name).Select(Literal)));
        builder.AppendLine(" };");
        builder.AppendLine();
        builder.AppendLine(
            "    private static global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolDescriptor[] CreateTools() => new global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolDescriptor[]"
        );
        builder.AppendLine("    {");
        foreach (ToolModel tool in tools)
        {
            RenderTool(builder, tool);
        }
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine(
            "    private static global::Agw.Tools.Abstractions.Generated.IAgwGeneratedToolBlockRegistration[] CreateToolBlockRegistrations() => new global::Agw.Tools.Abstractions.Generated.IAgwGeneratedToolBlockRegistration[]"
        );
        builder.AppendLine("    {");
        foreach (ToolSetOwnerModel toolSet in toolSets.Where(static model => model.IsToolBlock))
        {
            builder.Append("        new ").Append(FullyQualified(toolSet.Type)).AppendLine("(),");
        }
        builder.AppendLine("    };");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void RenderTool(StringBuilder builder, ToolModel tool)
    {
        string typeName = FullyQualified(tool.Type);
        builder.AppendLine("        new global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolDescriptor(");
        builder.Append("            typeof(").Append(typeName).AppendLine("),");
        builder.Append("            ").Append(Literal(tool.Name)).AppendLine(",");
        builder.Append("            ").Append(Literal(tool.DisplayName)).AppendLine(",");
        builder.Append("            ").Append(Literal(tool.Description)).AppendLine(",");
        builder.Append("            ").Append(Literal(tool.Category)).AppendLine(",");
        builder
            .Append("            (global::Agw.Tools.Abstractions.AgwToolPermission)")
            .Append(tool.Permission.ToString(CultureInfo.InvariantCulture))
            .AppendLine(",");
        builder.Append("            ").Append(Bool(tool.AllowInPlanMode)).AppendLine(",");
        builder.Append("            ").Append(Bool(tool.ExcludeFromList)).AppendLine(",");
        builder.Append("            ").Append(Bool(tool.RequiresWorkspace)).AppendLine(",");
        builder.Append("            ").Append(tool.TimeoutMs.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        builder.Append("            ").Append(Bool(tool.IsAsync)).AppendLine(",");
        builder.Append("            ").Append(Literal(tool.JsonSchema)).AppendLine(",");
        builder
            .Append("            ")
            .Append(tool.ReturnJsonSchema == null ? "null" : Literal(tool.ReturnJsonSchema))
            .AppendLine(",");
        builder.AppendLine(
            "            new global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolParameterDescriptor[]"
        );
        builder.AppendLine("            {");
        foreach (
            ParameterModel parameter in tool.Parameters.Where(static parameter =>
                !parameter.IsService && !parameter.IsCancellationToken
            )
        )
        {
            builder
                .Append(
                    "                new global::Agw.Tools.Abstractions.Generated.AgwGeneratedToolParameterDescriptor("
                )
                .Append(Literal(parameter.Symbol.Name))
                .Append(", ")
                .Append(Literal(parameter.Symbol.Type.ToDisplayString()))
                .Append(", ")
                .Append(parameter.Description == null ? "null" : Literal(parameter.Description))
                .Append(", ")
                .Append(Bool(parameter.HasDefault))
                .Append(", ")
                .Append(RenderObjectLiteral(parameter.DefaultValue, parameter.Symbol.Type))
                .Append(", ")
                .Append(parameter.SchemaType == null ? "null" : Literal(parameter.SchemaType))
                .Append(", ")
                .Append(parameter.Format == null ? "null" : Literal(parameter.Format))
                .Append(", ")
                .Append(
                    parameter.EnumValues == null
                        ? "null"
                        : "new string[] { " + string.Join(", ", parameter.EnumValues.Select(Literal)) + " }"
                )
                .AppendLine("),");
        }
        builder.AppendLine("            },");
        builder.AppendLine("            static async (context, cancellationToken) =>");
        builder.AppendLine("            {");
        string invocationTarget = tool.Method.IsStatic ? typeName : $"context.GetRequiredService<{typeName}>()";
        string arguments = string.Join(", ", tool.Parameters.Select(RenderArgument));
        string invocation = $"{invocationTarget}.{tool.Method.Name}({arguments})";
        if (tool.Method.ReturnType.SpecialType == SpecialType.System_Void)
        {
            builder.Append("                ").Append(invocation).AppendLine(";");
            builder.AppendLine("                return null;");
        }
        else if (IsNonGenericTaskLike(tool.Method.ReturnType))
        {
            builder.Append("                await ").Append(invocation).AppendLine(".ConfigureAwait(false);");
            builder.AppendLine("                return null;");
        }
        else if (tool.IsAsync)
        {
            builder.Append("                return await ").Append(invocation).AppendLine(".ConfigureAwait(false);");
        }
        else
        {
            builder.Append("                return ").Append(invocation).AppendLine(";");
        }
        builder.AppendLine("            }),");
    }

    private static string RenderArgument(ParameterModel parameter)
    {
        string type = FullyQualified(parameter.Symbol.Type);
        if (parameter.IsCancellationToken)
        {
            return "cancellationToken";
        }
        if (parameter.IsService)
        {
            return $"context.GetRequiredService<{type}>()";
        }
        if (parameter.HasDefault)
        {
            return $"context.GetOptionalArgument<{type}>({Literal(parameter.Symbol.Name)}, {RenderTypedLiteral(parameter.DefaultValue, parameter.Symbol.Type)})";
        }
        return $"context.GetRequiredArgument<{type}>({Literal(parameter.Symbol.Name)})";
    }

    private static string BuildInputSchema(IEnumerable<ParameterModel> parameters)
    {
        ParameterModel[] values = parameters
            .Where(static parameter => !parameter.IsService && !parameter.IsCancellationToken)
            .ToArray();
        var properties = new List<string>();
        foreach (ParameterModel parameter in values)
        {
            string schema = BuildTypeSchema(
                parameter.Symbol.Type,
                parameter,
                new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)
            );
            properties.Add(LiteralJson(parameter.Symbol.Name) + ":" + schema);
        }
        string required = string.Join(
            ",",
            values
                .Where(static parameter => !parameter.HasDefault)
                .Select(parameter => LiteralJson(parameter.Symbol.Name))
        );
        return "{\"type\":\"object\",\"properties\":{"
            + string.Join(",", properties)
            + "},\"required\":["
            + required
            + "]}";
    }

    private static string BuildTypeSchema(ITypeSymbol type, ParameterModel? parameter, HashSet<ITypeSymbol> visiting)
    {
        ITypeSymbol? underlying = null;
        bool nullable =
            type.NullableAnnotation == NullableAnnotation.Annotated || IsNullableValueType(type, out underlying);
        if (underlying != null)
        {
            type = underlying;
        }
        string schema = BuildNonNullTypeSchema(type, visiting);
        if (parameter != null)
        {
            schema = ApplyParameterSchema(schema, parameter);
            if (parameter.Description != null)
            {
                schema = AddProperty(schema, "description", LiteralJson(parameter.Description));
            }
            if (parameter.HasDefault)
            {
                schema = AddProperty(schema, "default", RenderJsonValue(parameter.DefaultValue));
            }
        }
        return nullable ? AddNull(schema) : schema;
    }

    private static string BuildNonNullTypeSchema(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            string values = string.Join(
                ",",
                type.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(static field => field.HasConstantValue)
                    .Select(field => LiteralJson(field.Name))
            );
            return "{\"type\":\"string\",\"enum\":[" + values + "]}";
        }
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Char:
                return "{\"type\":\"string\"}";
            case SpecialType.System_Boolean:
                return "{\"type\":\"boolean\"}";
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return "{\"type\":\"integer\"}";
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return "{\"type\":\"number\"}";
        }
        string display = type.ToDisplayString();
        if (display == "System.Guid")
            return "{\"type\":\"string\",\"format\":\"uuid\"}";
        if (display == "System.DateTimeOffset")
            return "{\"type\":\"string\",\"format\":\"date-time\"}";

        if (type is IArrayTypeSymbol array)
        {
            return "{\"type\":\"array\",\"items\":" + BuildTypeSchema(array.ElementType, null, visiting) + "}";
        }
        if (type is INamedTypeSymbol dictionary && TryGetStringDictionaryValue(dictionary, out ITypeSymbol? valueType))
        {
            return "{\"type\":\"object\",\"additionalProperties\":" + BuildTypeSchema(valueType!, null, visiting) + "}";
        }
        if (type is INamedTypeSymbol named && TryGetCollectionElement(named, out ITypeSymbol? element))
        {
            return "{\"type\":\"array\",\"items\":" + BuildTypeSchema(element!, null, visiting) + "}";
        }
        if (!visiting.Add(type))
        {
            return "{}";
        }
        var properties = new List<string>();
        var required = new List<string>();
        foreach (
            IPropertySymbol property in type.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(static property =>
                    property.DeclaredAccessibility == Accessibility.Public
                    && !property.IsStatic
                    && property.GetMethod != null
                )
        )
        {
            if (HasAttribute(property, "System.Text.Json.Serialization.JsonIgnoreAttribute"))
                continue;
            string jsonName =
                GetConstructorString(
                    GetAttribute(property, "System.Text.Json.Serialization.JsonPropertyNameAttribute"),
                    0
                ) ?? property.Name;
            string propertySchema = BuildTypeSchema(property.Type, null, visiting);
            string description = GetDescription(property);
            if (!string.IsNullOrWhiteSpace(description))
                propertySchema = AddProperty(propertySchema, "description", LiteralJson(description));
            properties.Add(LiteralJson(jsonName) + ":" + propertySchema);
            if (property.IsRequired || property.NullableAnnotation != NullableAnnotation.Annotated)
                required.Add(LiteralJson(jsonName));
        }
        visiting.Remove(type);
        return "{\"type\":\"object\",\"properties\":{"
            + string.Join(",", properties)
            + "},\"required\":["
            + string.Join(",", required)
            + "]}";
    }

    private static bool IsSupportedType(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        if (type is ITypeParameterSymbol || type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.Dynamic)
            return false;
        if (IsNullableValueType(type, out ITypeSymbol? underlying))
            return IsSupportedType(underlying!, visiting);
        if (type.TypeKind == TypeKind.Enum || type.SpecialType != SpecialType.None)
            return type.SpecialType != SpecialType.System_Object;
        string display = type.ToDisplayString();
        if (display == "System.Guid" || display == "System.DateTimeOffset")
            return true;
        if (type is IArrayTypeSymbol array)
            return IsSupportedType(array.ElementType, visiting);
        if (type is INamedTypeSymbol dictionary && TryGetStringDictionaryValue(dictionary, out ITypeSymbol? value))
            return IsSupportedType(value!, visiting);
        if (type is INamedTypeSymbol named && TryGetCollectionElement(named, out ITypeSymbol? element))
            return IsSupportedType(element!, visiting);
        if (!visiting.Add(type))
            return false;
        bool supported =
            type.TypeKind is TypeKind.Class or TypeKind.Struct
            && !HasAttribute(type, "System.Text.Json.Serialization.JsonConverterAttribute")
            && !HasAttribute(type, "System.Text.Json.Serialization.JsonPolymorphicAttribute")
            && type.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(static property =>
                    property.DeclaredAccessibility == Accessibility.Public
                    && !property.IsStatic
                    && property.GetMethod != null
                )
                .All(property =>
                    !HasAttribute(property, "System.Text.Json.Serialization.JsonConverterAttribute")
                    && !HasAttribute(property, "System.Text.Json.Serialization.JsonExtensionDataAttribute")
                    && IsSupportedType(property.Type, visiting)
                );
        visiting.Remove(type);
        return supported;
    }

    private static string ApplyParameterSchema(string schema, ParameterModel parameter)
    {
        if (parameter.SchemaType != null)
            schema = ReplaceType(schema, parameter.SchemaType);
        if (parameter.Format != null)
            schema = AddProperty(schema, "format", LiteralJson(parameter.Format));
        if (parameter.EnumValues != null)
            schema = AddProperty(
                schema,
                "enum",
                "[" + string.Join(",", parameter.EnumValues.Select(LiteralJson)) + "]"
            );
        AttributeData? attr = parameter.SchemaAttribute;
        if (attr != null)
        {
            AddNamedNumber(ref schema, attr, "Minimum", "minimum");
            AddNamedNumber(ref schema, attr, "Maximum", "maximum");
            AddNamedNumber(ref schema, attr, "MinLength", "minLength");
            AddNamedNumber(ref schema, attr, "MaxLength", "maxLength");
            string? pattern = GetNamedString(attr, "Pattern");
            if (pattern != null)
                schema = AddProperty(schema, "pattern", LiteralJson(pattern));
        }
        return schema;
    }

    private static void AddNamedNumber(ref string schema, AttributeData attr, string sourceName, string jsonName)
    {
        TypedConstant? value = attr
            .NamedArguments.Where(pair => pair.Key == sourceName)
            .Select(pair => (TypedConstant?)pair.Value)
            .FirstOrDefault();
        if (value?.Value != null)
            schema = AddProperty(schema, jsonName, Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "0");
    }

    private static string AddNull(string schema)
    {
        const string typePrefix = "{\"type\":\"";
        if (schema.StartsWith(typePrefix, StringComparison.Ordinal))
        {
            int end = schema.IndexOf('"', typePrefix.Length);
            string type = schema.Substring(typePrefix.Length, end - typePrefix.Length);
            return "{\"type\":[\"" + type + "\",\"null\"]" + schema.Substring(end + 1);
        }
        return "{\"anyOf\":[" + schema + ",{\"type\":\"null\"}]}";
    }

    private static string ReplaceType(string schema, string type)
    {
        return new Regex("\\\"type\\\":(?:\\\"[^\\\"]+\\\"|\\[[^\\]]+\\])").Replace(
            schema,
            "\"type\":\"" + EscapeJson(type) + "\"",
            1
        );
    }

    private static string AddProperty(string schema, string name, string value) =>
        schema == "{}"
            ? "{\"" + name + "\":" + value + "}"
            : schema.Insert(schema.Length - 1, ",\"" + name + "\":" + value);

    private static bool TryGetCollectionElement(INamedTypeSymbol type, out ITypeSymbol? element)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces.Concat(new[] { type }))
        {
            if (
                candidate.IsGenericType
                && candidate.ConstructedFrom.ToDisplayString()
                    is "System.Collections.Generic.IEnumerable<T>"
                        or "System.Collections.Generic.IReadOnlyList<T>"
                        or "System.Collections.Generic.IList<T>"
                        or "System.Collections.Generic.List<T>"
            )
            {
                element = candidate.TypeArguments[0];
                return true;
            }
        }
        element = null;
        return false;
    }

    private static bool TryGetStringDictionaryValue(INamedTypeSymbol type, out ITypeSymbol? value)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces.Concat(new[] { type }))
        {
            if (
                candidate.IsGenericType
                && candidate.ConstructedFrom.ToDisplayString()
                    is "System.Collections.Generic.IDictionary<TKey, TValue>"
                        or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                        or "System.Collections.Generic.Dictionary<TKey, TValue>"
                && candidate.TypeArguments[0].SpecialType == SpecialType.System_String
            )
            {
                value = candidate.TypeArguments[1];
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool IsNullableValueType(ITypeSymbol type, out ITypeSymbol? underlying)
    {
        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            underlying = named.TypeArguments[0];
            return true;
        }
        underlying = null;
        return false;
    }

    private static bool IsTaskLike(ITypeSymbol type, out ITypeSymbol? resultType)
    {
        if (type is INamedTypeSymbol named && named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
        {
            if (named.Name is "Task" or "ValueTask")
            {
                resultType = named.IsGenericType ? named.TypeArguments[0] : null;
                return true;
            }
        }
        resultType = null;
        return false;
    }

    private static bool IsNonGenericTaskLike(ITypeSymbol type) =>
        IsTaskLike(type, out ITypeSymbol? resultType) && resultType == null;

    private static string GetDescription(ISymbol symbol)
    {
        string? description = GetDescriptionAttribute(symbol, DescriptionAttribute);
        if (description != null)
            return description;
        string xml =
            symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default) ?? string.Empty;
        Match match = Regex.Match(xml, "<summary>\\s*(.*?)\\s*</summary>", RegexOptions.Singleline);
        return match.Success ? Regex.Replace(match.Groups[1].Value, "\\s+", " ").Trim() : string.Empty;
    }

    private static string? GetParameterDescription(IParameterSymbol parameter)
    {
        string xml =
            parameter.ContainingSymbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default)
            ?? string.Empty;
        Match match = Regex.Match(
            xml,
            "<param\\s+name=\"" + Regex.Escape(parameter.Name) + "\">\\s*(.*?)\\s*</param>",
            RegexOptions.Singleline
        );
        return match.Success ? Regex.Replace(match.Groups[1].Value, "\\s+", " ").Trim() : null;
    }

    private static string? GetDescriptionAttribute(ISymbol symbol, string metadataName) =>
        GetConstructorString(GetAttribute(symbol, metadataName), 0);

    private static bool HasAttribute(ISymbol symbol, string metadataName) => GetAttribute(symbol, metadataName) != null;

    private static AttributeData? GetAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string? GetConstructorString(AttributeData? attribute, int index) =>
        attribute != null
        && attribute.ConstructorArguments.Length > index
        && attribute.ConstructorArguments[index].Value is string value
            ? value
            : null;

    private static int? GetConstructorInt(AttributeData? attribute, int index) =>
        attribute != null
        && attribute.ConstructorArguments.Length > index
        && attribute.ConstructorArguments[index].Value != null
            ? Convert.ToInt32(attribute.ConstructorArguments[index].Value, CultureInfo.InvariantCulture)
            : null;

    private static int? GetToolPermission(AttributeData? attribute)
    {
        if (attribute == null)
            return null;
        if (
            attribute.ConstructorArguments.Length == 1
            && attribute.ConstructorArguments[0].Type?.SpecialType != SpecialType.System_String
        )
            return GetConstructorInt(attribute, 0);
        if (attribute.ConstructorArguments.Length == 2)
            return GetConstructorInt(attribute, 1);
        return null;
    }

    private static bool HasNamedArgument(AttributeData? attribute, string name) =>
        attribute?.NamedArguments.Any(pair => pair.Key == name) == true;

    private static string? GetNamedString(AttributeData? attribute, string name) =>
        attribute?.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string;

    private static bool? GetNamedBool(AttributeData? attribute, string name) =>
        attribute
            ?.NamedArguments.Where(pair => pair.Key == name)
            .Select(pair => pair.Value.Value as bool?)
            .FirstOrDefault();

    private static int? GetNamedInt(AttributeData? attribute, string name) =>
        attribute
            ?.NamedArguments.Where(pair => pair.Key == name)
            .Select(pair =>
                pair.Value.Value == null ? null : (int?)Convert.ToInt32(pair.Value.Value, CultureInfo.InvariantCulture)
            )
            .FirstOrDefault();

    private static string TrimAsyncSuffix(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;

    private static string FullyQualified(ITypeSymbol symbol) =>
        symbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                    | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            )
        );

    private static IReadOnlyList<INamedTypeSymbol> GetToolSetTypes(INamedTypeSymbol? type) =>
        type?.AllInterfaces.Where(static @interface =>
                @interface.IsGenericType && @interface.OriginalDefinition.ToDisplayString() == ToolSetInterface
            )
            .Select(static @interface => @interface.TypeArguments[0])
            .OfType<INamedTypeSymbol>()
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToArray()
        ?? [];

    private static bool HasInterface(INamedTypeSymbol type, string metadataName) =>
        type.AllInterfaces.Any(@interface => @interface.ToDisplayString() == metadataName);

    private static string GetAccessibility(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "internal",
        };

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

    private static string SanitizeHintName(string value) => Regex.Replace(value, "[^A-Za-z0-9_]", "_");

    private static string BuildGeneratedNamespace(string assemblyName) =>
        "Agw.Generated." + string.Join(".", assemblyName.Split('.').Select(SanitizeNamespaceSegment));

    private static string SanitizeNamespaceSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return "_";
        }

        var builder = new StringBuilder(segment.Length + 1);
        for (int index = 0; index < segment.Length; index++)
        {
            char character = segment[index];
            if (index == 0 && SyntaxFacts.IsIdentifierPartCharacter(character))
            {
                if (!SyntaxFacts.IsIdentifierStartCharacter(character))
                {
                    builder.Append('_');
                }
                builder.Append(character);
            }
            else
            {
                builder.Append(SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_');
            }
        }

        string identifier = builder.ToString();
        return SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None ? identifier : "_" + identifier;
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string LiteralJson(string value) => "\"" + EscapeJson(value) + "\"";

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string RenderJsonValue(object? value) =>
        value == null ? "null"
        : value is bool boolean ? Bool(boolean)
        : value is string text ? LiteralJson(text)
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

    private static string RenderObjectLiteral(object? value, ITypeSymbol type) =>
        value == null ? "null" : RenderTypedLiteral(value, type);

    private static string RenderTypedLiteral(object? value, ITypeSymbol type)
    {
        if (value == null)
            return "default";
        if (value is string text)
            return Literal(text);
        if (value is char character)
            return SymbolDisplay.FormatLiteral(character, quote: true);
        if (value is bool boolean)
            return Bool(boolean);
        if (type.TypeKind == TypeKind.Enum)
            return "("
                + FullyQualified(type)
                + ")"
                + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        string suffix = type.SpecialType switch
        {
            SpecialType.System_Single => "F",
            SpecialType.System_Double => "D",
            SpecialType.System_Decimal => "M",
            SpecialType.System_Int64 => "L",
            SpecialType.System_UInt64 => "UL",
            SpecialType.System_UInt32 => "U",
            _ => string.Empty,
        };
        return Convert.ToString(value, CultureInfo.InvariantCulture) + suffix;
    }

    private static void Report(
        SourceProductionContext context,
        DiagnosticDescriptor descriptor,
        ISymbol symbol,
        params object[] arguments
    ) => context.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), arguments));

    private sealed class ToolModel
    {
        public ToolModel(
            INamedTypeSymbol type,
            IMethodSymbol method,
            string name,
            string displayName,
            string description,
            string category,
            int permission,
            bool allowInPlanMode,
            bool excludeFromList,
            bool requiresWorkspace,
            int timeoutMs,
            bool isAsync,
            string jsonSchema,
            string? returnJsonSchema,
            IReadOnlyList<ParameterModel> parameters
        )
        {
            Type = type;
            Method = method;
            Name = name;
            DisplayName = displayName;
            Description = description;
            Category = category;
            Permission = permission;
            AllowInPlanMode = allowInPlanMode;
            ExcludeFromList = excludeFromList;
            RequiresWorkspace = requiresWorkspace;
            TimeoutMs = timeoutMs;
            IsAsync = isAsync;
            JsonSchema = jsonSchema;
            ReturnJsonSchema = returnJsonSchema;
            Parameters = parameters;
        }

        public INamedTypeSymbol Type { get; }
        public IMethodSymbol Method { get; }
        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string Category { get; }
        public int Permission { get; }
        public bool AllowInPlanMode { get; }
        public bool ExcludeFromList { get; }
        public bool RequiresWorkspace { get; }
        public int TimeoutMs { get; }
        public bool IsAsync { get; }
        public string JsonSchema { get; }
        public string? ReturnJsonSchema { get; }
        public IReadOnlyList<ParameterModel> Parameters { get; }
    }

    private sealed class ParameterModel
    {
        public ParameterModel(
            IParameterSymbol symbol,
            string? description,
            bool isService,
            bool isCancellationToken,
            bool hasDefault,
            object? defaultValue,
            string? schemaType,
            string? format,
            string[]? enumValues,
            AttributeData? schemaAttribute
        )
        {
            Symbol = symbol;
            Description = description;
            IsService = isService;
            IsCancellationToken = isCancellationToken;
            HasDefault = hasDefault;
            DefaultValue = defaultValue;
            SchemaType = schemaType;
            Format = format;
            EnumValues = enumValues;
            SchemaAttribute = schemaAttribute;
        }

        public IParameterSymbol Symbol { get; }
        public string? Description { get; }
        public bool IsService { get; }
        public bool IsCancellationToken { get; }
        public bool HasDefault { get; }
        public object? DefaultValue { get; }
        public string? SchemaType { get; }
        public string? Format { get; }
        public string[]? EnumValues { get; }
        public AttributeData? SchemaAttribute { get; }
    }

    private sealed class ToolSetOwnerModel
    {
        public ToolSetOwnerModel(INamedTypeSymbol type, IReadOnlyList<INamedTypeSymbol> toolTypes, bool isToolBlock)
        {
            Type = type;
            ToolTypes = toolTypes;
            IsToolBlock = isToolBlock;
        }

        public INamedTypeSymbol Type { get; }
        public IReadOnlyList<INamedTypeSymbol> ToolTypes { get; }
        public bool IsToolBlock { get; }
    }
}
