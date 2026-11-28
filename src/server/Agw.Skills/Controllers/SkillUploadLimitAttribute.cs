using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Agw.Skills.Controllers;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class SkillUploadLimitAttribute : Attribute, IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var factories = context.ValueProviderFactories;
        for (var index = 0; index < factories.Count; index++)
        {
            if (factories[index] is FormValueProviderFactory form)
            {
                factories[index] = new SkillFormValueProviderFactory(form);
                break;
            }
        }

        return next();
    }

    private sealed class SkillFormValueProviderFactory : IValueProviderFactory
    {
        private readonly FormValueProviderFactory _form;

        public SkillFormValueProviderFactory(FormValueProviderFactory form)
        {
            _form = form;
        }

        public async Task CreateValueProviderAsync(ValueProviderFactoryContext context)
        {
            try
            {
                await _form.CreateValueProviderAsync(context);
            }
            catch (ValueProviderException exception)
                when (exception.InnerException
                        is BadHttpRequestException { StatusCode: StatusCodes.Status413RequestEntityTooLarge }
                )
            {
                // 保留 Kestrel 的 413 状态码。Preserve Kestrel's HTTP 413 status.
                throw new AgwException(ErrorCodes.FileTooLarge);
            }
        }
    }
}
