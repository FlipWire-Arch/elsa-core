﻿using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Elsa.Scripting.Liquid.Extensions;
using Elsa.Scripting.Liquid.Options;
using Fluid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Elsa.Scripting.Liquid.Services
{
    public class LiquidTemplateManager : ILiquidTemplateManager
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IServiceProvider _serviceProvider;
        private readonly LiquidOptions _options;

        public LiquidTemplateManager(IMemoryCache memoryCache, IOptions<LiquidOptions> options, IServiceProvider serviceProvider)
        {
            this._memoryCache = memoryCache;
            this._serviceProvider = serviceProvider;
            this._options = options.Value;
        }

        public async Task<string> RenderAsync(string source, TemplateContext context, TextEncoder encoder)
        {
            if (string.IsNullOrWhiteSpace(source))
                return default;

            context.AddAsyncFilters(_options, _serviceProvider);
            var result = GetCachedTemplate(source);

            return await result.RenderAsync(context, encoder);
        }

        private FluidTemplate GetCachedTemplate(string source)
        {
            IEnumerable<string> errors;

            var result = _memoryCache.GetOrCreate(
                source,
                e =>
                {
                    if (!TryParse(source, out var parsed, out errors))
                        TryParse(string.Join(Environment.NewLine, errors), out parsed, out errors);

                    e.SetSlidingExpiration(TimeSpan.FromSeconds(30));
                    return parsed;
                });
            return result;
        }

        public bool Validate(string template, out IEnumerable<string> errors) => TryParse(template, out _, out errors);
        private bool TryParse(string template, out FluidTemplate result, out IEnumerable<string> errors) => FluidTemplate.TryParse(template, true, out result, out errors);
    }
}