using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Entities;

namespace GatewayService.Services.Coupon
{
    public interface ICouponRepository
    {
        Task<ApiResponse<CouponDTO>> Query(string coupon);
        Task<ApiResponse<CouponDTO>> Query(string from_platform, string tid);
        Task<ApiResponse<List<CouponDTO>>> Validate(string[] coupons,  byte? status=null);
        Task<ApiResponse<bool>> Invalidate(string coupon);
        Task<ApiResponse<PageResult<CouponDTO>>> PageQuery(int page,
            int size,
            byte? status = null,
            DateTime? from = null,
            DateTime? to = null);
        Task<ApiResponse<CouponDTO>> Generate(ExternalOrderDTO dto);

        Task<ApiResponse<CouponDTO>> GenerateManual(string from_platform, string tid, decimal amount);
        Task<ApiResponse<bool>> Delete(string coupon);
        Task<ApiResponse<bool>> Update(LuoliCommon.DTO.Coupon.UpdateRequest ur);


    }

}
