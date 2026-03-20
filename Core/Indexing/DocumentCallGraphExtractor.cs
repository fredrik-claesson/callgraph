using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CallGraph.Core.Indexing;

internal static class DocumentCallGraphExtractor
{
    public static async Task<DocumentCallGraph> ExtractAsync(
        Document doc,
        Func<Task<DispatchMaps>> getDispatchMaps,
        CancellationToken cancellationToken)
    {
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return new DocumentCallGraph([], []);

        var nodes = new List<Node>();
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<Edge>();
        var edgeKeys = new HashSet<EdgeKey>(EdgeKeyComparer.Instance);
        DispatchMaps? dispatchMaps = null;
        var sameTypeMethodsCache = new Dictionary<INamedTypeSymbol, IReadOnlyList<IMethodSymbol>>(SymbolEqualityComparer.Default);
        var methodKeyCache = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);

        foreach (var declaration in EnumerateCallableDeclarations(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var caller = model.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
            if (caller is null)
                continue;

            var callerKey = GetOrAddMethodKey(caller, methodKeyCache);
            AddDeclaredNode(nodes, nodeKeys, callerKey, caller, declaration.GetLocation());

            var rootOperation = GetBodyOperation(model, declaration, cancellationToken);
            var invocationOperationCount = 0;
            if (rootOperation is not null)
            {
                foreach (var operation in EnumerateOperations(rootOperation))
                {
                    switch (operation)
                    {
                        case IInvocationOperation invocation:
                        {
                            invocationOperationCount++;
                            var callee = invocation.TargetMethod;
                            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, callee, "calls-direct");

                            var isInterfaceCall = IsInterfaceCall(invocation, out var interfaceMethod);
                            var looksLikePublisherCall = LooksLikePublisherCall(callee);
                            if (isInterfaceCall || looksLikePublisherCall)
                                dispatchMaps ??= await getDispatchMaps().ConfigureAwait(false);

                            if (isInterfaceCall && dispatchMaps is not null)
                            {
                                AddInterfaceImplementationEdges(
                                    edges,
                                    edgeKeys,
                                    nodes,
                                    nodeKeys,
                                    methodKeyCache,
                                    callerKey,
                                    interfaceMethod!,
                                    dispatchMaps.InterfaceImplementations);
                            }

                            if (looksLikePublisherCall && dispatchMaps is not null)
                            {
                                AddPublishedMessageHandlerEdges(
                                    edges,
                                    edgeKeys,
                                    nodes,
                                    nodeKeys,
                                    methodKeyCache,
                                    callerKey,
                                    invocation,
                                    callee,
                                    dispatchMaps.MessageHandlers);
                            }
                            break;
                        }
                        case IObjectCreationOperation objectCreation when objectCreation.Constructor is not null:
                            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, objectCreation.Constructor, "calls-direct");
                            break;
                        case IDelegateCreationOperation delegateCreation:
                        {
                            var targetMethod = ExtractReferencedMethod(delegateCreation.Target);
                            if (targetMethod is not null)
                                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, targetMethod, "calls-via-delegate");
                            break;
                        }
                        case IPropertyReferenceOperation propertyReference:
                            AddPropertyAccessorEdges(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, propertyReference);
                            break;
                        case IEventAssignmentOperation eventAssignment:
                            AddEventAccessorEdges(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, eventAssignment);
                            break;
                    }
                }
            }

            // Fallback: recover direct invocation edges from syntax when Roslyn IOperation misses parts of a method body.
            // This protects private-method inbound indexing against false "unused" positives.
            if (rootOperation is null ||
                (invocationOperationCount == 0 && ContainsPotentialSameTypeInvocation(declaration)))
            {
                AddSyntaxInvocationEdges(
                    declaration,
                    caller,
                    callerKey,
                    edges,
                    edgeKeys,
                    nodes,
                    nodeKeys,
                    sameTypeMethodsCache,
                    methodKeyCache);
            }
        }

        return new DocumentCallGraph(nodes, edges);
    }

    private static IEnumerable<SyntaxNode> EnumerateCallableDeclarations(SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes())
        {
            if (declaration is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax)
                yield return declaration;
        }
    }

    private static IOperation? GetBodyOperation(
        SemanticModel model,
        SyntaxNode declaration,
        CancellationToken cancellationToken)
    {
        return declaration switch
        {
            BaseMethodDeclarationSyntax method when method.Body is not null => model.GetOperation(method.Body, cancellationToken),
            BaseMethodDeclarationSyntax method when method.ExpressionBody is not null => model.GetOperation(method.ExpressionBody.Expression, cancellationToken),
            LocalFunctionStatementSyntax localFunction when localFunction.Body is not null => model.GetOperation(localFunction.Body, cancellationToken),
            LocalFunctionStatementSyntax localFunction when localFunction.ExpressionBody is not null => model.GetOperation(localFunction.ExpressionBody.Expression, cancellationToken),
            AccessorDeclarationSyntax accessor when accessor.Body is not null => model.GetOperation(accessor.Body, cancellationToken),
            AccessorDeclarationSyntax accessor when accessor.ExpressionBody is not null => model.GetOperation(accessor.ExpressionBody.Expression, cancellationToken),
            _ => null
        };
    }

    private static IEnumerable<IOperation> EnumerateOperations(IOperation rootOperation)
    {
        var stack = new Stack<IOperation>();
        stack.Push(rootOperation);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            foreach (var child in current.ChildOperations.Reverse())
            {
                if (child is ILocalFunctionOperation)
                    continue;

                stack.Push(child);
            }
        }
    }

    private static void AddSyntaxInvocationEdges(
        SyntaxNode declaration,
        IMethodSymbol caller,
        string callerKey,
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<INamedTypeSymbol, IReadOnlyList<IMethodSymbol>> sameTypeMethodsCache,
        IDictionary<IMethodSymbol, string> methodKeyCache)
    {
        var bodySyntax = GetBodySyntax(declaration);
        if (bodySyntax is null)
            return;

        var containingType = caller.ContainingType;
        var sameTypeMethods = containingType is null
            ? null
            : GetOrAddSameTypeMethods(containingType, sameTypeMethodsCache);

        foreach (var invocation in bodySyntax.DescendantNodes(n => n is not LocalFunctionStatementSyntax).OfType<InvocationExpressionSyntax>())
        {
            var callee = TryResolveSameTypeInvokedMethod(invocation, containingType, sameTypeMethods);
            if (callee is null)
                continue;

            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, callee, "calls-direct");
        }
    }

    private static SyntaxNode? GetBodySyntax(SyntaxNode declaration)
    {
        return declaration switch
        {
            BaseMethodDeclarationSyntax method when method.Body is not null => method.Body,
            BaseMethodDeclarationSyntax method when method.ExpressionBody is not null => method.ExpressionBody.Expression,
            LocalFunctionStatementSyntax localFunction when localFunction.Body is not null => localFunction.Body,
            LocalFunctionStatementSyntax localFunction when localFunction.ExpressionBody is not null => localFunction.ExpressionBody.Expression,
            AccessorDeclarationSyntax accessor when accessor.Body is not null => accessor.Body,
            AccessorDeclarationSyntax accessor when accessor.ExpressionBody is not null => accessor.ExpressionBody.Expression,
            _ => null
        };
    }

    private static IMethodSymbol? TryResolveSameTypeInvokedMethod(
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol? containingType,
        IReadOnlyList<IMethodSymbol>? sameTypeMethods)
    {
        if (!TryGetSameTypeInvokedMethodName(invocation.Expression, out var methodName))
            return null;

        if (containingType is null || sameTypeMethods is null || sameTypeMethods.Count == 0)
            return null;

        var argumentCount = invocation.ArgumentList.Arguments.Count;
        var candidates = sameTypeMethods
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .Where(m => IsArityCompatible(m, argumentCount))
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        var exactArity = candidates.Where(m => m.Parameters.Length == argumentCount).ToList();
        return exactArity.Count == 1 ? exactArity[0] : null;
    }

    private static bool TryGetSameTypeInvokedMethodName(ExpressionSyntax expression, out string methodName)
    {
        methodName = string.Empty;

        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                methodName = identifier.Identifier.ValueText;
                return true;

            case GenericNameSyntax generic:
                methodName = generic.Identifier.ValueText;
                return true;

            case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax or BaseExpressionSyntax:
                if (memberAccess.Name is GenericNameSyntax genericMember)
                {
                    methodName = genericMember.Identifier.ValueText;
                    return true;
                }

                methodName = memberAccess.Name.Identifier.ValueText;
                return true;

            default:
                return false;
        }
    }

    private static bool IsArityCompatible(IMethodSymbol method, int argumentCount)
    {
        var parameters = method.Parameters;
        if (parameters.Length == 0)
            return argumentCount == 0;

        var requiredCount = parameters.Count(p => !p.IsOptional && !p.IsParams);
        if (argumentCount < requiredCount)
            return false;

        if (parameters[^1].IsParams)
            return true;

        return argumentCount <= parameters.Length;
    }

    private static void AddMethodEdge(
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<IMethodSymbol, string> methodKeyCache,
        string callerKey,
        IMethodSymbol callee,
        string kind)
    {
        var calleeKey = GetOrAddMethodKey(callee, methodKeyCache);
        if (AddEdge(edges, edgeKeys, callerKey, calleeKey, kind))
            TryAddSourceNode(nodes, nodeKeys, methodKeyCache, callee);
    }

    private static bool AddEdge(
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        string from,
        string to,
        string kind)
    {
        if (!edgeKeys.Add(new EdgeKey(from, to, kind)))
            return false;

        edges.Add(new Edge
        {
            From = from,
            To = to,
            Direction = "outbound",
            Kind = kind
        });
        return true;
    }

    private static void TryAddSourceNode(
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<IMethodSymbol, string> methodKeyCache,
        IMethodSymbol method)
    {
        var loc = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc?.SourceTree?.FilePath is null)
            return;

        var methodKey = GetOrAddMethodKey(method, methodKeyCache);
        if (!nodeKeys.Add(methodKey))
            return;

        nodes.Add(IndexedCallableNodeFactory.Create(method, loc));
    }

    private static bool IsInterfaceCall(IInvocationOperation invocation, out IMethodSymbol? interfaceMethod)
    {
        interfaceMethod = null;

        if (invocation.TargetMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            interfaceMethod = invocation.TargetMethod;
            return true;
        }

        if (invocation.Instance?.Type is INamedTypeSymbol { TypeKind: TypeKind.Interface } &&
            invocation.TargetMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            interfaceMethod = invocation.TargetMethod;
            return true;
        }

        return false;
    }

    private static void AddInterfaceImplementationEdges(
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<IMethodSymbol, string> methodKeyCache,
        string callerKey,
        IMethodSymbol interfaceMethod,
        Dictionary<string, List<INamedTypeSymbol>> interfaceImplementations)
    {
        var interfaceType = interfaceMethod.ContainingType;
        if (interfaceType is null)
            return;

        var interfaceKey = interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!interfaceImplementations.TryGetValue(interfaceKey, out var implementations))
            return;

        foreach (var implementingType in implementations)
        {
            var implementationMethod = MethodSignatureMatcher.FindImplementationMethod(implementingType, interfaceMethod);
            if (implementationMethod is null)
                continue;

            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, implementationMethod, "calls-via-interface");
        }
    }

    private static void AddPublishedMessageHandlerEdges(
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<IMethodSymbol, string> methodKeyCache,
        string callerKey,
        IInvocationOperation invocation,
        IMethodSymbol callee,
        Dictionary<string, List<IMethodSymbol>> messageHandlers)
    {
        if (!TryGetPublishedMessageType(invocation, callee, out var payloadType))
            return;

        var payloadKey = payloadType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!messageHandlers.TryGetValue(payloadKey, out var handlers))
            return;

        foreach (var handler in handlers)
        {
            var handlerKey = GetOrAddMethodKey(handler, methodKeyCache);
            if (string.Equals(handlerKey, callerKey, StringComparison.Ordinal))
                continue;

            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, handler, "calls-via-message");
        }
    }

    private static bool TryGetPublishedMessageType(
        IInvocationOperation invocation,
        IMethodSymbol callee,
        out ITypeSymbol? payloadType)
    {
        payloadType = null;

        if (!LooksLikePublisherCall(callee))
            return false;

        if (callee.IsGenericMethod && callee.TypeArguments.Length > 0)
        {
            var typeArgument = callee.TypeArguments[0];
            if (IsMessagePayloadType(typeArgument))
            {
                payloadType = typeArgument;
                return true;
            }
        }

        foreach (var argument in invocation.Arguments)
        {
            var type = argument.Value.Type;
            if (!IsMessagePayloadType(type))
                continue;

            payloadType = type;
            return true;
        }

        return false;
    }

    private static bool LooksLikePublisherCall(IMethodSymbol callee)
    {
        if (ContainsPublisherVerb(callee.Name))
            return true;

        var containingType = callee.ContainingType?.Name ?? string.Empty;
        return containingType.Contains("Event", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Bus", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Mediator", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Publisher", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Dispatcher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPublisherVerb(string methodName)
        => methodName.Contains("Publish", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Emit", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Dispatch", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Raise", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Send", StringComparison.OrdinalIgnoreCase);

    private static bool IsMessagePayloadType(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        if (type.SpecialType != SpecialType.None)
            return false;

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (string.Equals(fullName, "global::System.String", StringComparison.Ordinal)
            || string.Equals(fullName, "global::System.Threading.CancellationToken", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsPotentialSameTypeInvocation(SyntaxNode declaration)
    {
        var bodySyntax = GetBodySyntax(declaration);
        if (bodySyntax is null)
            return false;

        foreach (var invocation in bodySyntax.DescendantNodes(n => n is not LocalFunctionStatementSyntax).OfType<InvocationExpressionSyntax>())
        {
            if (TryGetSameTypeInvokedMethodName(invocation.Expression, out _))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<IMethodSymbol> GetOrAddSameTypeMethods(
        INamedTypeSymbol containingType,
        IDictionary<INamedTypeSymbol, IReadOnlyList<IMethodSymbol>> cache)
    {
        if (cache.TryGetValue(containingType, out var cached))
            return cached;

        var methods = containingType
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction)
            .ToList();
        cache[containingType] = methods;
        return methods;
    }

    private static void AddDeclaredNode(
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        string methodKey,
        IMethodSymbol method,
        Location location)
    {
        if (!nodeKeys.Add(methodKey))
            return;

        nodes.Add(IndexedCallableNodeFactory.Create(method, location));
    }

    private static string GetOrAddMethodKey(IMethodSymbol method, IDictionary<IMethodSymbol, string> methodKeyCache)
    {
        if (methodKeyCache.TryGetValue(method, out var key))
            return key;

        key = SymbolKeyFormatter.Format(method);
        methodKeyCache[method] = key;
        return key;
    }

    private static IMethodSymbol? ExtractReferencedMethod(IOperation? operation)
    {
        return operation switch
        {
            IMethodReferenceOperation methodReference => methodReference.Method,
            IConversionOperation conversion => ExtractReferencedMethod(conversion.Operand),
            IParenthesizedOperation parenthesized => ExtractReferencedMethod(parenthesized.Operand),
            _ => null
        };
    }

    private static void AddPropertyAccessorEdges(
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<IMethodSymbol, string> methodKeyCache,
        string callerKey,
        IPropertyReferenceOperation propertyReference)
    {
        if (propertyReference.Parent is INameOfOperation)
            return;

        var property = propertyReference.Property;
        var parent = propertyReference.Parent;

        if (parent is ISimpleAssignmentOperation simpleAssignment && ReferenceEquals(simpleAssignment.Target, propertyReference))
        {
            if (property.SetMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, property.SetMethod, "calls-via-property-set");
            return;
        }

        if (parent is ICompoundAssignmentOperation compoundAssignment && ReferenceEquals(compoundAssignment.Target, propertyReference))
        {
            if (property.GetMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, property.GetMethod, "calls-via-property-get");
            if (property.SetMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, property.SetMethod, "calls-via-property-set");
            return;
        }

        if (parent is IIncrementOrDecrementOperation incrementOrDecrement && ReferenceEquals(incrementOrDecrement.Target, propertyReference))
        {
            if (property.GetMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, property.GetMethod, "calls-via-property-get");
            if (property.SetMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, property.SetMethod, "calls-via-property-set");
            return;
        }

        if (property.GetMethod is not null)
            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, property.GetMethod, "calls-via-property-get");
    }

    private static void AddEventAccessorEdges(
        ICollection<Edge> edges,
        HashSet<EdgeKey> edgeKeys,
        ICollection<Node> nodes,
        ISet<string> nodeKeys,
        IDictionary<IMethodSymbol, string> methodKeyCache,
        string callerKey,
        IEventAssignmentOperation eventAssignment)
    {
        if (eventAssignment.EventReference is not IEventReferenceOperation eventReference)
            return;

        var eventSymbol = eventReference.Event;
        if (eventAssignment.Adds)
        {
            if (eventSymbol.AddMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, eventSymbol.AddMethod, "calls-via-event-add");
        }
        else
        {
            if (eventSymbol.RemoveMethod is not null)
                AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, eventSymbol.RemoveMethod, "calls-via-event-remove");
        }

        var handlerMethod = ExtractReferencedMethod(eventAssignment.HandlerValue);
        if (handlerMethod is not null)
            AddMethodEdge(edges, edgeKeys, nodes, nodeKeys, methodKeyCache, callerKey, handlerMethod, "calls-via-event-handler");
    }

    private readonly record struct EdgeKey(string From, string To, string Kind);

    private sealed class EdgeKeyComparer : IEqualityComparer<EdgeKey>
    {
        public static EdgeKeyComparer Instance { get; } = new();

        public bool Equals(EdgeKey x, EdgeKey y)
            => string.Equals(x.From, y.From, StringComparison.Ordinal)
               && string.Equals(x.To, y.To, StringComparison.Ordinal)
               && string.Equals(x.Kind, y.Kind, StringComparison.Ordinal);

        public int GetHashCode(EdgeKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.From, StringComparer.Ordinal);
            hash.Add(obj.To, StringComparer.Ordinal);
            hash.Add(obj.Kind, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
