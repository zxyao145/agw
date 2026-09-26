using System.Text.RegularExpressions;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts;

namespace Agw.Skills.Domain.Behaviors;

public sealed class SkillBehavior
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;
    private static readonly Regex SkillNamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private readonly Skill _skill;

    public SkillBehavior(Skill skill)
    {
        _skill = skill;
    }

    /// <summary>
    /// <para>内置技能由系统提供：Kind 为 BuiltIn，或 Id 属于已注册的内置技能。</para>
    /// <para>A built-in skill comes from the system: its kind is BuiltIn, or its ID belongs to a registered built-in skill.</para>
    /// </summary>
    public bool IsBuiltIn(bool isRegisteredBuiltIn) => _skill.Kind == SkillKind.BuiltIn || isRegisteredBuiltIn;

    public void EnsureMutable(bool isRegisteredBuiltIn)
    {
        if (IsBuiltIn(isRegisteredBuiltIn))
        {
            throw new AgwException(ErrorCodes.BuiltInSkillImmutable);
        }
    }

    /// <summary>
    /// <para>本地技能创建时必须上传归档包且不能带远程地址；远程技能不接受归档包；其他 Kind 不能通过 API 创建。</para>
    /// <para>A local skill needs an archive and no remote URL; a remote skill takes no archive; other kinds cannot be created through the API.</para>
    /// </summary>
    public void EnsureCreatable(bool archiveSupplied)
    {
        switch (_skill.Kind)
        {
            case SkillKind.Local:
                if (!archiveSupplied)
                {
                    throw new AgwException(ErrorCodes.SkillArchiveCannotBeEmpty);
                }

                EnsureNoRemoteUrl(_skill.RemoteUrl);
                break;
            case SkillKind.Remote:
                EnsureNoArchive(archiveSupplied);
                break;
            default:
                throw new AgwException(ErrorCodes.SkillKindInvalid);
        }
    }

    /// <summary>
    /// <para>本地技能不能带远程地址，改名时必须同时上传新的归档包，SKILL.md 才能保持一致；远程技能不接受归档包。</para>
    /// <para>A local skill takes no remote URL and needs a new archive when renamed so SKILL.md stays consistent; a remote skill takes no archive.</para>
    /// </summary>
    public void EnsureUpdatable(string name, bool archiveSupplied, string? remoteUrl)
    {
        switch (_skill.Kind)
        {
            case SkillKind.Local:
                EnsureNoRemoteUrl(remoteUrl);
                if (!string.Equals(_skill.Name, name.Trim(), StringComparison.Ordinal) && !archiveSupplied)
                {
                    throw new AgwException(
                        ErrorCodes.SkillNameUpdateRequiresArchive,
                        "Updating the skill name requires uploading a new archive so SKILL.md can stay consistent."
                    );
                }

                break;
            case SkillKind.Remote:
                EnsureNoArchive(archiveSupplied);
                break;
            default:
                throw new AgwException(ErrorCodes.SkillKindInvalid);
        }
    }

    public void DefineLocal(string name, string description)
    {
        Define(name, description);
        _skill.RemoteUrl = null;
    }

    public void DefineRemote(string remoteUrl, string name, string description)
    {
        Define(name, description);
        _skill.RemoteUrl = remoteUrl;
    }

    public void EnsureRemote()
    {
        if (_skill.Kind != SkillKind.Remote || string.IsNullOrWhiteSpace(_skill.RemoteUrl))
        {
            throw new AgwException(ErrorCodes.RemoteSkillConfigurationInvalid);
        }
    }

    /// <summary>
    /// <para>远程技能以名称作为身份：远程定义改名后，已保存的技能不能继续使用它。</para>
    /// <para>A remote skill's name is its identity: once the remote definition is renamed, the saved skill cannot use it.</para>
    /// </summary>
    public void EnsureRemoteIdentity(string definitionName)
    {
        if (!string.Equals(definitionName, _skill.Name, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.RemoteSkillIdentityChanged);
        }
    }

    private static void EnsureNoRemoteUrl(string? remoteUrl)
    {
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new AgwException(ErrorCodes.SkillKindInvalid, "Local skills cannot define a remote URL.");
        }
    }

    private static void EnsureNoArchive(bool archiveSupplied)
    {
        if (archiveSupplied)
        {
            throw new AgwException(ErrorCodes.RemoteSkillArchiveNotAllowed);
        }
    }

    /// <summary>
    /// <para>名称与描述去除两端空白后保存；本地技能的内容位于以稳定 Id 命名的目录中。</para>
    /// <para>Name and description are stored trimmed; a local skill's content lives in a folder named by its stable ID.</para>
    /// </summary>
    private void Define(string name, string description)
    {
        ValidateName(name);
        ValidateDescription(description);
        _skill.Name = name.Trim();
        _skill.Description = description.Trim();
        _skill.Id = _skill.Id == Guid.Empty ? Guid.CreateVersion7() : _skill.Id;
        _skill.ContentPath = _skill.Kind == SkillKind.Local ? $"skills/{_skill.Id:N}" : string.Empty;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AgwException(ErrorCodes.SkillNameRequired);
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new AgwException(
                ErrorCodes.SkillNameTooLong,
                $"Skill name must be {MaxNameLength} characters or fewer."
            );
        }

        if (!SkillNamePattern.IsMatch(trimmed))
        {
            throw new AgwException(
                ErrorCodes.SkillNameInvalidFormat,
                "Skill name must contain only lowercase letters, numbers, and single hyphens."
            );
        }
    }

    private static void ValidateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new AgwException(ErrorCodes.SkillDescriptionRequired);
        }

        if (description.Trim().Length > MaxDescriptionLength)
        {
            throw new AgwException(
                ErrorCodes.SkillDescriptionTooLong,
                $"Skill description must be {MaxDescriptionLength} characters or fewer."
            );
        }
    }
}
