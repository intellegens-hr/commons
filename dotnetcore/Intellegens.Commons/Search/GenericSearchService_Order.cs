﻿using Intellegens.Commons.Search.Models;
using Intellegens.Commons.Types;
using Microsoft.EntityFrameworkCore.Internal;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Dynamic.Core;

namespace Intellegens.Commons.Search
{
    public partial class GenericSearchService<T>
        where T : class, new()
    {
        private const string exprIfTrueThen1 = " ? 1 : 0 ";
        private const string exprIfTrueThen0 = " ? 0 : 1 ";

        /// <summary>
        /// For given SearchOrder model, generates string to place in OrderBy
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        private string GetOrderingString(SearchOrder order)
        {
            var propertyChainInfo = TypeUtils.GetPropertyInfoPerPathSegment<T>(order.Key).ToList();
            // this part will split entire path:
            // if input path is a.b.c -> output will be it.a.b.c == value
            // if input path is a.b[].c -> output will be it.a.b.Any(xyz1 => xyz1.c == value)
            List<string> pathSegmentsResolved = new List<string>();
            int bracketsOpen = 0;
            for (int i = 0; i < propertyChainInfo.Count(); i++)
            {
                var (_, propertyInfo, isCollectionType) = propertyChainInfo[i];
                if (isCollectionType)
                {
                    pathSegmentsResolved.Add($"{propertyInfo.Name}.Min(xyz{i} => xyz{i}");
                    bracketsOpen++;
                }
                else
                {
                    pathSegmentsResolved.Add(propertyInfo.Name);
                }
            }

            return $"it.{string.Join(".", pathSegmentsResolved)}{new String(')', bracketsOpen)} {(order.Ascending ? "ascending" : "descending")}";
        }

        /// <summary>
        /// Combines multiple query parts into one in order to use them in order by match count.
        /// Different query parts should be connected with "+" sign
        /// </summary>
        /// <param name="queryParts"></param>
        /// <param name="logicalOperator"></param>
        /// <returns></returns>
        private (string expression, object[] arguments) CombineQueryPartsAndArgumentsAsHitCount(IEnumerable<(string expression, object[] arguments)> queryParts, LogicOperators logicalOperator)
        {
            var parameters = new List<object>();
            string query = "";

            // Take all expressions and add IIF( ? 1 : 0) if IIF is not already present (? 1 : 0 or ? 0 : 1 if entire expression was negated
            // at some point
            List<(string expression, object[] arguments)> queryPartsFiltered = queryParts
                .Where(x => !string.IsNullOrEmpty(x.expression))
                .Select(x => ((x.expression.Contains(exprIfTrueThen1) || x.expression.Contains(exprIfTrueThen0)) 
                                ? x.expression : $"({x.expression} {exprIfTrueThen1})", x.arguments))
                .ToList();

            // Combines multiple query parts with + operator
            if (queryPartsFiltered.Any())
            {
                query = string.Join($" + ", queryPartsFiltered.Select(x => x.expression));
                queryPartsFiltered.Select(x => x.arguments).ToList().ForEach(x => parameters.AddRange(x));
                query = $"( {query} )";
            }

            return (query, parameters.ToArray());
        }

        /// <summary>
        /// Process criteria for Order by match count.
        /// Very similar to ProcessCriteria but uses different method for combining multiple criteria
        /// and has different negation logic
        /// </summary>
        /// <param name="searchCriteria"></param>
        /// <returns></returns>
        private (string expression, object[] parameters) ProcessCriteriaOrderBy(SearchCriteria searchCriteria)
        {
            var keysOrValuesDefined = (searchCriteria.Keys?.Any() ?? false) || (searchCriteria.Values?.Any() ?? false);
            var nestedFiltersDefined = searchCriteria.Criteria?.Any() ?? false;

            // if keys are not defined and values are - this is full text search
            if (!searchCriteria.Keys.Any() && searchCriteria.Values.Any())
            {
                searchCriteria.Keys = FullTextSearchPaths;
                searchCriteria.KeysLogic = LogicOperators.ANY;
            }

            // if nested filters are defined and keys/values as well - keys and values will be treated as another SearchCriteria
            if (keysOrValuesDefined && nestedFiltersDefined)
            {
                searchCriteria.Criteria.Add(new SearchCriteria
                {
                    Keys = searchCriteria.Keys,
                    KeysLogic = searchCriteria.KeysLogic,
                    Operator = searchCriteria.Operator,
                    Values = searchCriteria.Values,
                    ValuesLogic = searchCriteria.ValuesLogic
                });
            }
            (string expression, object[] arguments) combinedQueryParts = ("", null);
            if (nestedFiltersDefined)
            {
                var criterias = searchCriteria.Criteria.Select(x => ProcessCriteriaOrderBy(x));
                combinedQueryParts = CombineQueryPartsAndArgumentsAsHitCount(criterias, searchCriteria.CriteriaLogic);
            }
            else if (keysOrValuesDefined)
            {
                var values = searchCriteria.Values ?? new List<string>();

                var keys = searchCriteria.Keys ?? new List<string>();

                var expressions = keys
                    .Select(key => GetFilterExpression(key, values, searchCriteria.Operator, searchCriteria.ValuesLogic))
                    .ToList();

                combinedQueryParts = CombineQueryPartsAndArgumentsAsHitCount(expressions, searchCriteria.KeysLogic);
            }

            if (!string.IsNullOrEmpty(combinedQueryParts.expression))
            {
                combinedQueryParts.expression = $" ({combinedQueryParts.expression}) ";

                // when switching one expression to other and back, one of these expressions must be stored as something else
                const string switchReplacementValue = ">>*?TempReplacement?*<<";

                // if entire expression must be negated, this means that expression inside brackets needs to be
                // inverted: "? 1 : 0" to "? 0 : 1" and vice-versa
                if (searchCriteria.Negate) { 
                    combinedQueryParts.expression = combinedQueryParts
                        .expression
                        .Replace(exprIfTrueThen1, switchReplacementValue) // replacement value is not a valid string and will not already be inside expression
                        .Replace(exprIfTrueThen0, exprIfTrueThen1)
                        .Replace(switchReplacementValue, exprIfTrueThen0);
                }
            }

            return combinedQueryParts;
        }

        /// <summary>
        /// Apply OrderBy to query
        /// </summary>
        /// <param name="sourceData"></param>
        /// <param name="searchRequest"></param>
        /// <returns></returns>
        protected IQueryable<T> OrderQuery(IQueryable<T> sourceData, SearchRequest searchRequest)
        {
            var orderByItems = searchRequest
                .Order
                .Where(x => !string.IsNullOrEmpty(x.Key))
                .ToList();

            bool firstOrderByPassed = false;

            // if order by math count was set, generate expression and use it as first order by
            if (searchRequest.OrderByMatchCount)
            {
                var (expression, parameters) = ProcessCriteriaOrderBy(searchRequest);
                if (!string.IsNullOrEmpty(expression))
                {
                    string expressionWithParamsReplaced = ReplaceParametersPlaceholder(expression);
                    sourceData = sourceData.OrderBy(parsingConfig, $"{expressionWithParamsReplaced} DESC", parameters);
                    firstOrderByPassed = true;
                }
            }

            foreach (var item in orderByItems)
            {
                var ordering = GetOrderingString(item);

                if (firstOrderByPassed)
                    sourceData = (sourceData as IOrderedQueryable<T>).ThenBy(ordering);
                else
                    sourceData = sourceData.OrderBy(ordering);

                firstOrderByPassed = true;
            }

            return sourceData;
        }
    }
}