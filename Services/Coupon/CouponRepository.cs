

using Grpc.Core;
using LuoliCommon.DTO.Coupon;
using LuoliCommon.DTO.ExternalOrder;

using LuoliCommon.Entities;
using LuoliUtils;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Drawing;
using System.Security.Cryptography;
using ThirdApis;

namespace GatewayService.Services.Coupon
{
    public class CouponRepository : ICouponRepository
    {
        private readonly AsynsApis _asynsApis;
        public CouponRepository(AsynsApis asynsApis)
        {
            _asynsApis = asynsApis;
        }


        public async Task<ApiResponse<bool>> Delete(string coupon)
        {
            return await _asynsApis.CouponDelete(coupon);
        }

        public async Task<ApiResponse<CouponDTO>> Generate(ExternalOrderDTO dto)
        {
            return await _asynsApis.CouponGenerate(dto);
        }

        public async Task<ApiResponse<CouponDTO>> GenerateManual(string from_platform, string tid, decimal amount)
        {
            return await _asynsApis.CouponGenerateManual(from_platform,tid,amount);
        }

        public async Task<ApiResponse<bool>> Invalidate(string coupon)
        {
            return await _asynsApis.CouponInvalidate(coupon);
        }

        public async Task<ApiResponse<PageResult<CouponDTO>>> PageQuery(int page, int size, byte? status = null, DateTime? from = null, DateTime? to = null)
        {
            return await _asynsApis.CouponPageQuery(page, size, status, from,to);
        }

        public async Task<ApiResponse<CouponDTO>> Query(string coupon)
        {
            return await _asynsApis.CouponQuery(coupon);
        }

        public async Task<ApiResponse<CouponDTO>> Query(string from_platform, string tid)
        {
            return await _asynsApis.CouponQuery(from_platform, tid);
        }

        public async Task<ApiResponse<bool>> Update(LuoliCommon.DTO.Coupon.UpdateRequest ur)
        {
            return await _asynsApis.CouponUpdate(ur);
        }

        public async Task<ApiResponse<List<CouponDTO>>> Validate(string[] coupons, byte? status = null)
        {
            return await _asynsApis.CouponValidate(coupons, status);
        }
    }
}
