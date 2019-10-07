using Coldairarrow.Business.Cache;
using Coldairarrow.Entity.Base_Manage;
using Coldairarrow.Util;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Coldairarrow.Business.Base_Manage
{
    public class Base_UserBusiness : BaseBusiness<Base_User>, IBase_UserBusiness, IDependency
    {
        #region DI

        public Base_UserBusiness(IBase_UserDTOCache sysUserCache, IOperator @operator, IDataPermission dataPermission)
        {
            _sysUserCache = sysUserCache;
            _operator = @operator;
            _dataPermission = dataPermission;
        }
        IBase_UserDTOCache _sysUserCache { get; }
        IOperator _operator { get; }
        IDataPermission _dataPermission { get; }

        #endregion

        #region 重写

        public override IQueryable<Base_User> GetIQueryable()
        {
            return _dataPermission.GetIQ_Base_User(Service);
        }

        #endregion

        #region 外部接口

        public List<Base_UserDTO> GetDataList(Pagination pagination, bool all, string userId = null, string keyword = null)
        {
            Expression<Func<Base_User, Base_Department, Base_UserDTO>> select = (a, b) => new Base_UserDTO
            {
                DepartmentName = b.Name
            };
            select = select.BuildExtendSelectExpre();
            var q_User = all ? Service.GetIQueryable<Base_User>() : GetIQueryable();
            var q = from a in q_User.AsExpandable()
                    join b in Service.GetIQueryable<Base_Department>() on a.DepartmentId equals b.Id into ab
                    from b in ab.DefaultIfEmpty()
                    select @select.Invoke(a, b);

            var where = LinqHelper.True<Base_UserDTO>();
            if (!userId.IsNullOrEmpty())
                where = where.And(x => x.Id == userId);
            if (!keyword.IsNullOrEmpty())
            {
                where = where.And(x =>
                    EF.Functions.Like(x.UserName, keyword)
                    || EF.Functions.Like(x.RealName, keyword));
            }

            var list = q.Where(where).GetPagination(pagination).ToList();

            SetProperty(list);

            return list;

            void SetProperty(List<Base_UserDTO> users)
            {
                //补充用户角色属性
                List<string> userIds = users.Select(x => x.Id).ToList();
                var userRoles = (from a in Service.GetIQueryable<Base_UserRole>()
                                 join b in Service.GetIQueryable<Base_Role>() on a.RoleId equals b.Id
                                 where userIds.Contains(a.UserId)
                                 select new
                                 {
                                     a.UserId,
                                     RoleId = b.Id,
                                     b.RoleName
                                 }).ToList();
                users.ForEach(aUser =>
                {
                    var roleList = userRoles.Where(x => x.UserId == aUser.Id);
                    aUser.RoleIdList = roleList.Select(x => x.RoleId).ToList();
                    aUser.RoleNameList = roleList.Select(x => x.RoleName).ToList();
                });
            }
        }

        public Base_User GetTheData(string id)
        {
            return GetEntity(id);
        }

        public Base_UserDTO GetTheInfo(string userId)
        {
            return _sysUserCache.GetCache(userId);
        }

        [DataAddLog(LogType.系统用户管理, "RealName", "用户")]
        [DataRepeatValidate(
            new string[] { "UserName" },
            new string[] { "用户名" })]
        public AjaxResult AddData(Base_User newData)
        {
            Insert(newData);

            return Success();
        }

        [DataEditLog(LogType.系统用户管理, "RealName", "用户")]
        [DataRepeatValidate(
            new string[] { "UserName" },
            new string[] { "用户名" })]
        public AjaxResult UpdateData(Base_User theData)
        {
            if (theData.Id == "Admin" && _operator.UserId != theData.Id)
                return new ErrorResult("禁止更改超级管理员！");

            Update(theData);
            _sysUserCache.UpdateCache(theData.Id);

            return Success();
        }

        [DataDeleteLog(LogType.系统用户管理, "RealName", "用户")]
        public AjaxResult DeleteData(List<string> ids)
        {
            var adminUser = GetTheInfo("Admin");
            if (ids.Contains(adminUser.Id))
                return new ErrorResult("超级管理员是内置账号,禁止删除！");
            var userIds = GetIQueryable().Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToList();

            Delete(ids);
            _sysUserCache.UpdateCache(userIds);

            return Success();
        }

        #endregion

        #region 私有成员

        #endregion

        #region 数据模型

        #endregion
    }

    [MapFrom(typeof(Base_User))]
    public class Base_UserDTO : Base_User
    {
        public string RoleNames { get => string.Join(",", RoleNameList); }
        public List<string> RoleIdList { get; set; }
        public List<string> RoleNameList { get; set; }
        public EnumType.RoleType RoleType
        {
            get
            {
                int type = 0;

                var values = typeof(EnumType.RoleType).GetEnumValues();
                foreach (var aValue in values)
                {
                    if (RoleNames.Contains(aValue.ToString()))
                        type += (int)aValue;
                }

                return (EnumType.RoleType)type;
            }
        }
        public string DepartmentName { get; set; }
    }
}