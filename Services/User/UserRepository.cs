
namespace GatewayService.Services.User
{


    public class UserRepository : IUserRepository
    {

        //public MUser ValidateUser(string username, string password)
        //{
        //    string key = $"user.{username}";
        //    if(RedisHelper.Exists(key))
        //        return RedisHelper.Get<MUser>(key);

        //    var pwd = SSha256.ComputeHash(password);
        //    MUser user = Program.SqlClient.Queryable<MUser>()
        //        .Where(u =>
        //            u.username == username &&
        //            u.password == pwd)
        //        .First();

        //    if (user is null)
        //        return user;

        //    RedisHelper.Set(key, user, 3600);

        //    return user;
        //}

        
        //public MUser GetUser(string username)
        //{
        //    string key = $"user.{username}";
        //    if (RedisHelper.Exists(key))
        //        return RedisHelper.Get<MUser>(key);

        //    MUser user = Program.SqlClient.Queryable<MUser>()
        //        .Where(u =>
        //            u.username == username )
        //        .First();

        //    if (user is null)
        //        return user;

        //    RedisHelper.Set(key, user, 3600);

        //    return user;
        //}
    }
}
